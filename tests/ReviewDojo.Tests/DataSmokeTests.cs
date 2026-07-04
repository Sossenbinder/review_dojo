using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewDojo.Core;
using ReviewDojo.Data;
using Xunit;

public class DataSmokeTests
{
    static ReviewDojoContext NewCtx()
    {
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<ReviewDojoContext>().UseSqlite(conn).Options;
        var ctx = new ReviewDojoContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public void CanPersistSessionDiffAndManifest()
    {
        using var ctx = NewCtx();
        var s = new Session { TargetRepoPath = "/tmp/repo", DifficultyTier = DifficultyTier.Medium, Seed = 1, CreatedAt = DateTime.UtcNow };
        s.Diffs.Add(new Diff {
            Ordinal = 0, UnifiedDiffText = "diff", IsClean = false, SizeLines = 42,
            GeneratedAt = DateTime.UtcNow,
            Manifest = { new ManifestEntry { FilePath = "a.cs", LineStart = 1, LineEnd = 2, Category = BugCategory.Mechanical, Severity = Severity.High, Description = "x" } }
        });
        ctx.Sessions.Add(s); ctx.SaveChanges();

        var loaded = ctx.Diffs.Include(d => d.Manifest).Single();
        Assert.Single(loaded.Manifest);
        Assert.Equal(BugCategory.Mechanical, loaded.Manifest[0].Category);
    }
}
