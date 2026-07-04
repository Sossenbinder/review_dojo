using ReviewDojo.Core;
using ReviewDojo.Core.Scoring;
using Xunit;

public class ScoringTests
{
    static ManifestBug Bug(string f, int a, int b, BugCategory c) =>
        new(f, a, b, c, Severity.Medium, "d");
    static ReviewFinding Find(string f, int l, BugCategory c) => new(f, l, c, null);

    [Fact]
    public void ExactFileLineCategory_IsFullCredit()
    {
        var m = new FindingMatcher(proximity: 3);
        var res = m.Match(
            new[] { Bug("a.cs", 10, 12, BugCategory.Mechanical) },
            new[] { Find("a.cs", 11, BugCategory.Mechanical) });
        Assert.Single(res, r => r.Bug != null && r.Finding != null && r.Credit == 1.0);
    }

    [Fact]
    public void CategoryMismatchWithinWindow_IsHalfCredit()
    {
        var m = new FindingMatcher(proximity: 3);
        var res = m.Match(
            new[] { Bug("a.cs", 10, 10, BugCategory.Mechanical) },
            new[] { Find("a.cs", 10, BugCategory.EdgeCase) });
        Assert.Contains(res, r => r.Credit == 0.5 && !r.CategoryMatched);
    }

    [Fact]
    public void OutOfWindowFinding_IsFalsePositive()
    {
        var m = new FindingMatcher(proximity: 3);
        var res = m.Match(
            new[] { Bug("a.cs", 10, 10, BugCategory.Mechanical) },
            new[] { Find("a.cs", 50, BugCategory.Mechanical) });
        Assert.Contains(res, r => r.Bug == null && r.Finding != null); // FP
        Assert.Contains(res, r => r.Bug != null && r.Finding == null); // miss
    }

    [Fact]
    public void EachBugMatchesAtMostOnce()
    {
        var m = new FindingMatcher(proximity: 3);
        var res = m.Match(
            new[] { Bug("a.cs", 10, 10, BugCategory.Mechanical) },
            new[] { Find("a.cs", 10, BugCategory.Mechanical), Find("a.cs", 11, BugCategory.Mechanical) });
        Assert.Equal(1, res.Count(r => r.Bug != null && r.Finding != null));
        Assert.Equal(1, res.Count(r => r.Bug == null && r.Finding != null)); // second is FP
    }

    [Fact]
    public void CleanDiffApproved_IsPerfectAndCalibrated()
    {
        var matches = new List<MatchResult>(); // no bugs, no findings
        var card = ScoreCalculator.Compute(matches, Verdict.Approve, isClean: true, elapsedMs: 1000);
        Assert.Equal(1.0, card.Recall);
        Assert.Equal(1.0, card.Precision);
        Assert.True(card.VerdictCorrect);
    }

    [Fact]
    public void SeededDiffApprovedWithMiss_IsWrongVerdict()
    {
        var bug = new ManifestBug("a.cs",1,1,BugCategory.Mechanical,Severity.High,"d");
        var matches = new List<MatchResult> { new(bug, null, 0, false) };
        var card = ScoreCalculator.Compute(matches, Verdict.Approve, isClean: false, elapsedMs: 500);
        Assert.Equal(0.0, card.Recall);
        Assert.False(card.VerdictCorrect);
    }

    [Fact]
    public void SeverityWeightedRecall_WeightsCriticalHigher()
    {
        var low  = new ManifestBug("a.cs",1,1,BugCategory.Mechanical,Severity.Low,"d");
        var crit = new ManifestBug("a.cs",9,9,BugCategory.Mechanical,Severity.Critical,"d");
        var f    = new ReviewFinding("a.cs",1,BugCategory.Mechanical,null);
        var matches = new List<MatchResult> {
            new(low, f, 1.0, true),        // found the low one
            new(crit, null, 0, false) };   // missed the critical one
        var card = ScoreCalculator.Compute(matches, Verdict.RequestChanges, false, 100);
        Assert.Equal(0.5, card.Recall);                  // 1 of 2 by count
        Assert.True(card.SeverityWeightedRecall < 0.5);  // weighted below, critical missed
    }
}
