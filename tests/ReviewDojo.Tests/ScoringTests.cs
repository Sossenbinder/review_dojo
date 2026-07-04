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
}
