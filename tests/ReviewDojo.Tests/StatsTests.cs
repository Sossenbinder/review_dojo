using ReviewDojo.Api;
using ReviewDojo.Core;
using ReviewDojo.Data;
using Xunit;

public class StatsTests
{
    [Fact]
    public void PerCategoryRecall_CountsHitsOverManifestByCategory()
    {
        var diff = new Diff {
            IsClean = false, Verdict = Verdict.RequestChanges, SubmittedAt = DateTime.UtcNow,
            Manifest = {
                new ManifestEntry { Category = BugCategory.Mechanical, Severity = Severity.High },
                new ManifestEntry { Category = BugCategory.EdgeCase, Severity = Severity.Low } },
            Score = new ScoreRecord {
                Recall = 0.5, Precision = 1, VerdictCorrect = true, TimeMs = 1000, FalsePositiveRate = 0,
                MatchesJson = "[{\"BugFile\":\"a.cs\",\"BugLine\":1,\"FindingFile\":\"a.cs\",\"FindingLine\":1,\"Credit\":1.0}]" } };
        var stats = StatsBuilder.Build(new[] { diff });
        Assert.Equal(1, stats.TotalScored);
        Assert.True(stats.PerCategoryRecall.ContainsKey(nameof(BugCategory.Mechanical)));
    }

    [Fact]
    public void EmptyInput_ReturnsZeroedStats()
    {
        var stats = StatsBuilder.Build(System.Array.Empty<Diff>());
        Assert.Equal(0, stats.TotalScored);
        Assert.Empty(stats.PerCategoryRecall);
    }
}
