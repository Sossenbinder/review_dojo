using LibGit2Sharp;
namespace ReviewDojo.Cli;

public static class CorpusMiner
{
    static readonly string[] Keywords = { "fix", "revert", "bug", "patch", "hotfix" };

    public static bool LooksLikeFix(string message)
    {
        var m = message.ToLowerInvariant();
        return Keywords.Any(k => m.Contains(k));
    }

    public static IEnumerable<(string Sha, string Message, string Diff)> Mine(string repoPath, int max = 200)
    {
        using var repo = new Repository(repoPath);
        int taken = 0;
        foreach (var c in repo.Commits)
        {
            if (taken >= max) yield break;
            if (c.Parents.Count() != 1) continue;
            if (!LooksLikeFix(c.MessageShort)) continue;
            var patch = repo.Diff.Compare<Patch>(c.Parents.First().Tree, c.Tree);
            yield return (c.Sha, c.MessageShort, patch.Content);
            taken++;
        }
    }
}
