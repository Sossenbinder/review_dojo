using ReviewDojo.Core;
using ReviewDojo.Generator;
using Xunit;

public class MockAnthropicClientTests
{
    static string RepoPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "mini-repo");

    [Fact]
    public async Task Mock_SeededRun_ProducesResolvableManifest()
    {
        var gen = new DiffGenerator(new MockAnthropicClient(), "mock");
        var res = await gen.GenerateAsync(RepoPath, DifficultyTier.Medium, seed: 1, forceMistakeCount: 1);
        Assert.False(res.IsClean);
        Assert.NotEmpty(res.Manifest);
        foreach (var b in res.Manifest)
            Assert.InRange(b.LineStart, 1, 9999);
        // the fixture's "if (n == 0)" becomes "if (n = 0)" — the flipped line must be in the diff
        Assert.Contains("if (n = 0)", res.UnifiedText);
    }

    [Fact]
    public async Task Mock_CleanRun_IsCleanButNonEmptyDiff()
    {
        var gen = new DiffGenerator(new MockAnthropicClient(), "mock");
        var res = await gen.GenerateAsync(RepoPath, DifficultyTier.Medium, seed: 2, forceMistakeCount: 0);
        Assert.True(res.IsClean);
        Assert.Empty(res.Manifest);
        Assert.Contains("+", res.UnifiedText); // benign change present (an added line)
    }
}
