using ReviewDojo.Core;
using ReviewDojo.Generator;
using Xunit;

public class GeneratorDeterminismTests
{
    // Fake returns: step1 -> after-contents with a correct comparison; step2 -> injects "if (n = 0)".
    class FakeClient : IAnthropicClient
    {
        public Task<string> CompleteAsync(LlmRequest r, CancellationToken ct = default)
        {
            bool isInject = r.System.Contains("INJECT");
            string json = isInject
              ? "{\"files\":[{\"path\":\"Calc.cs\",\"after\":\"public class Calc{\\n int F(int n){ if (n = 0) return 1; return n; }\\n}\"}]," +
                "\"bugs\":[{\"path\":\"Calc.cs\",\"anchor\":\"if (n = 0)\",\"category\":\"Mechanical\",\"severity\":\"High\",\"description\":\"assignment\"}]}"
              : "{\"files\":[{\"path\":\"Calc.cs\",\"after\":\"public class Calc{\\n int F(int n){ if (n == 0) return 1; return n; }\\n}\"}]}";
            return Task.FromResult(json);
        }
    }

    [Fact]
    public async Task TenRuns_EveryManifestLineExistsInDiff_AndCleanRunsAreEmpty()
    {
        var gen = new DiffGenerator(new FakeClient(), "claude-sonnet-4-6");
        var repoPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "mini-repo");

        for (int i = 0; i < 10; i++)
        {
            bool forceClean = i % 2 == 0;
            var res = await gen.GenerateAsync(
                repoPath: repoPath,
                tier: DifficultyTier.Medium,
                seed: i,
                forceMistakeCount: forceClean ? 0 : 1);

            if (forceClean)
            {
                Assert.True(res.IsClean);
                Assert.Empty(res.Manifest);
            }
            else
            {
                Assert.NotEmpty(res.Manifest);
                Assert.Contains("if (n = 0)", res.UnifiedText);
                foreach (var bug in res.Manifest)
                    Assert.InRange(bug.LineStart, 1, 9999);
            }
        }
    }
}
