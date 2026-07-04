using ReviewDojo.Core;
using ReviewDojo.Data;
namespace ReviewDojo.Api;

public record StatsDto(
    int TotalScored, double MeanRecall, double MeanPrecision, double MeanFpRate,
    double MedianTimeMs, Dictionary<string,double> PerCategoryRecall,
    double CleanApprovalRate, double SeededRejectionRate, List<double> RecallTrend);

public static class StatsBuilder
{
    public static StatsDto Build(IReadOnlyCollection<Diff> scored)
    {
        if (scored.Count == 0)
            return new(0,0,0,0,0,new(),0,0,new());

        var times = scored.Select(d => (double)d.Score!.TimeMs).OrderBy(x => x).ToList();
        double median = times[times.Count / 2];

        var perCat = new Dictionary<string,(double hit,double total)>();
        foreach (var d in scored)
        {
            var hitCount = System.Text.Json.JsonSerializer
                .Deserialize<List<MatchDto>>(d.Score!.MatchesJson)!
                .Count(m => m.BugFile != null && m.FindingFile != null);
            var byCat = d.Manifest.GroupBy(m => m.Category.ToString());
            int manifestTotal = d.Manifest.Count;
            foreach (var g in byCat)
            {
                double share = manifestTotal == 0 ? 0 : (double)g.Count() / manifestTotal * hitCount;
                var cur = perCat.GetValueOrDefault(g.Key, (0,0));
                perCat[g.Key] = (cur.hit + share, cur.total + g.Count());
            }
        }
        var perCatRecall = perCat.ToDictionary(kv => kv.Key,
            kv => kv.Value.total == 0 ? 0 : kv.Value.hit / kv.Value.total);

        var clean = scored.Where(d => d.IsClean).ToList();
        var seeded = scored.Where(d => !d.IsClean).ToList();
        double cleanApproval = clean.Count == 0 ? 0 : (double)clean.Count(d => d.Verdict == Verdict.Approve) / clean.Count;
        double seededReject  = seeded.Count == 0 ? 0 : (double)seeded.Count(d => d.Verdict == Verdict.RequestChanges) / seeded.Count;

        var trend = scored.OrderBy(d => d.SubmittedAt).Select(d => d.Score!.Recall).ToList();

        return new(scored.Count, scored.Average(d => d.Score!.Recall), scored.Average(d => d.Score!.Precision),
            scored.Average(d => d.Score!.FalsePositiveRate), median, perCatRecall,
            cleanApproval, seededReject, trend);
    }
}
