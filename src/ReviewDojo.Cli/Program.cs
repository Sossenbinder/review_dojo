using Microsoft.EntityFrameworkCore;
using ReviewDojo.Cli;
using ReviewDojo.Core;
using ReviewDojo.Data;
using ReviewDojo.Generator;

if (args.Length < 2) { Console.WriteLine("usage: dojo <mine|gen> <repoPath> [--seed N]"); return; }

switch (args[0])
{
    case "mine":
    {
        var repo = args[1];
        var opts = new DbContextOptionsBuilder<ReviewDojoContext>()
            .UseSqlite("Data Source=reviewdojo.db").Options;
        using var db = new ReviewDojoContext(opts);
        db.Database.Migrate();
        int n = 0;
        foreach (var (sha, msg, diff) in CorpusMiner.Mine(repo))
        {
            db.Corpus.Add(new BugCorpusEntry {
                RepoPath = repo, Category = BugCategory.Mechanical,
                BeforeSnippet = "", AfterSnippet = diff.Length > 4000 ? diff[..4000] : diff,
                CommitSha = sha, Message = msg });
            n++;
        }
        db.SaveChanges();
        Console.WriteLine($"Mined {n} fix/revert commits into corpus.");
        break;
    }
    case "gen":
    {
        var repo = args[1];
        int seed = args.Contains("--seed") ? int.Parse(args[Array.IndexOf(args, "--seed") + 1]) : 1;
        using var http = new HttpClient();
        var gen = new DiffGenerator(new AnthropicClient(http),
            Environment.GetEnvironmentVariable("DOJO_MODEL") ?? "claude-sonnet-4-6");
        var g = await gen.GenerateAsync(repo, DifficultyTier.Medium, seed);
        Console.WriteLine(g.UnifiedText);
        Console.WriteLine($"\n--- MANIFEST ({g.Manifest.Count}) ---");
        foreach (var b in g.Manifest)
            Console.WriteLine($"{b.FilePath}:{b.LineStart}-{b.LineEnd} [{b.Category}/{b.Severity}] {b.Description}");
        break;
    }
    default: Console.WriteLine("unknown command"); break;
}
