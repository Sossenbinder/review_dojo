using ReviewDojo.Core;
namespace ReviewDojo.Generator;

public record LocusFile(string RelPath, string Text);
public record BugFewShot(BugCategory Category, string Before, string After, string Message);

public static class LocusSelector
{
    static readonly string[] Exts = { ".cs", ".ts", ".js", ".py", ".go", ".java", ".rb" };
    public static List<LocusFile> Select(string repoPath, DifficultyTier tier, int seed)
    {
        var all = Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
            .Where(p => Exts.Contains(Path.GetExtension(p)))
            .Where(p =>
            {
                // Only exclude bin/obj/.git that appear WITHIN the repo, not in the
                // repoPath prefix itself (which may legitimately live under bin/ in tests).
                var rel = Path.GetRelativePath(repoPath, p);
                var sep = Path.DirectorySeparatorChar;
                return !rel.Contains($"{sep}bin{sep}")
                    && !rel.Contains($"{sep}obj{sep}")
                    && !rel.Contains($"{sep}.git{sep}");
            })
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (all.Count == 0) throw new InvalidOperationException($"No source files under {repoPath}");
        int count = tier == DifficultyTier.Hard ? 2 : 1;
        var rng = new Random(seed);
        int start = rng.Next(all.Count);
        return Enumerable.Range(0, count)
            .Select(i => all[(start + i) % all.Count])
            .Select(p => new LocusFile(Path.GetRelativePath(repoPath, p), File.ReadAllText(p)))
            .ToList();
    }
}

public static class MistakeCountPolicy
{
    public static int Decide(DifficultyTier tier, int seed, double cleanRate)
    {
        var rng = new Random(seed);
        if (rng.NextDouble() < cleanRate) return 0;
        int k = tier switch { DifficultyTier.Easy => 1, DifficultyTier.Medium => 2, DifficultyTier.Hard => 3, _ => 1 };
        return 1 + rng.Next(k);
    }
}

public static class PromptLibrary
{
    public const string LegitChangeSystem =
        "You are a senior engineer producing a single realistic, correct code change " +
        "(feature, refactor, or fix) to the provided files. Return ONLY JSON: " +
        "{\"files\":[{\"path\":\"...\",\"after\":\"<full new file contents>\"}]}. No prose.";

    public const string InjectSystem =
        "INJECT MODE. You are given already-correct edited files. Introduce EXACTLY the " +
        "requested number of realistic bugs drawn from the given categories, the kind an " +
        "AI code assistant plausibly makes. Return ONLY JSON: {\"files\":[{\"path\",\"after\"}]," +
        "\"bugs\":[{\"path\",\"anchor\",\"category\",\"severity\",\"description\"}]}. " +
        "The anchor MUST be a verbatim substring of a changed/added line so it can be located. No prose.";

    public static string LegitChangeUser(Dictionary<string,string> before, DifficultyTier tier)
    {
        var files = string.Join("\n", before.Select(kv => $"### {kv.Key}\n{kv.Value}"));
        return $"Difficulty: {tier}. Make one plausible change across these files:\n{files}";
    }

    public static string InjectUser(Dictionary<string,string> after, int m, DifficultyTier tier, IReadOnlyList<BugFewShot>? shots)
    {
        var cats = tier switch {
            DifficultyTier.Easy => "Mechanical",
            DifficultyTier.Medium => "Mechanical, EdgeCase",
            _ => "Mechanical, EdgeCase, Contextual" };
        var files = string.Join("\n", after.Select(kv => $"### {kv.Key}\n{kv.Value}"));
        var examples = shots is { Count: > 0 }
            ? "\nReal-bug examples for inspiration:\n" + string.Join("\n", shots.Select(s => $"[{s.Category}] {s.Message}\n- {s.Before}\n+ {s.After}"))
            : "";
        return $"Inject EXACTLY {m} bug(s) from categories: {cats}.{examples}\nFiles:\n{files}";
    }
}
