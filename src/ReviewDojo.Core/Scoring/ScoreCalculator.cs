namespace ReviewDojo.Core.Scoring;

public record ScoreCard(
    double Recall, double Precision, double SeverityWeightedRecall,
    double FalsePositiveRate, bool VerdictCorrect, long ElapsedMs,
    int Hits, int Misses, int FalsePositives);

public static class ScoreCalculator
{
    static double Weight(Severity s) => s switch
    { Severity.Low => 1, Severity.Medium => 2, Severity.High => 3, Severity.Critical => 5, _ => 1 };

    public static ScoreCard Compute(
        IReadOnlyList<MatchResult> matches, Verdict verdict, bool isClean, long elapsedMs)
    {
        var bugMatches   = matches.Where(m => m.Bug != null).ToList();
        var findMatches  = matches.Where(m => m.Finding != null).ToList();
        int bugCount     = bugMatches.Count;
        int findCount    = findMatches.Count;
        double creditSum = matches.Where(m => m.Bug != null && m.Finding != null).Sum(m => m.Credit);

        double recall    = bugCount == 0 ? 1.0 : creditSum / bugCount;
        double precision = findCount == 0 ? 1.0 : creditSum / findCount;

        double wTotal = bugMatches.Sum(m => Weight(m.Bug!.Severity));
        double wHit   = bugMatches.Where(m => m.Finding != null).Sum(m => m.Credit * Weight(m.Bug!.Severity));
        double sevRecall = wTotal == 0 ? 1.0 : wHit / wTotal;

        int fp = matches.Count(m => m.Bug == null && m.Finding != null);
        double fpRate = findCount == 0 ? 0.0 : (double)fp / findCount;

        bool verdictCorrect = isClean ? verdict == Verdict.Approve : verdict == Verdict.RequestChanges;
        int hits   = matches.Count(m => m.Bug != null && m.Finding != null);
        int misses = matches.Count(m => m.Bug != null && m.Finding == null);

        return new ScoreCard(recall, precision, sevRecall, fpRate, verdictCorrect, elapsedMs, hits, misses, fp);
    }
}
