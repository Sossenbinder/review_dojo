using Microsoft.EntityFrameworkCore;
using ReviewDojo.Core;

namespace ReviewDojo.Data;

public class Session
{
    public int Id { get; set; }
    public string TargetRepoPath { get; set; } = "";
    public DifficultyTier DifficultyTier { get; set; }
    public double CleanRate { get; set; } = 0.2;
    public int Seed { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Diff> Diffs { get; set; } = new();
}

public class Diff
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public int Ordinal { get; set; }
    public string UnifiedDiffText { get; set; } = "";
    public bool IsClean { get; set; }
    public int SizeLines { get; set; }
    public int Seed { get; set; }
    public DateTime GeneratedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public Verdict? Verdict { get; set; }
    public List<ManifestEntry> Manifest { get; set; } = new();  // server-only
    public List<FindingRecord> Findings { get; set; } = new();
    public ScoreRecord? Score { get; set; }
}

public class ManifestEntry
{
    public int Id { get; set; }
    public int DiffId { get; set; }
    public string FilePath { get; set; } = "";
    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public BugCategory Category { get; set; }
    public Severity Severity { get; set; }
    public string Description { get; set; } = "";
}

public class FindingRecord
{
    public int Id { get; set; }
    public int DiffId { get; set; }
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public BugCategory Category { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ScoreRecord
{
    public int Id { get; set; }
    public int DiffId { get; set; }
    public double Recall { get; set; }
    public double Precision { get; set; }
    public double SeverityWeightedRecall { get; set; }
    public double FalsePositiveRate { get; set; }
    public bool VerdictCorrect { get; set; }
    public long TimeMs { get; set; }
    public string MatchesJson { get; set; } = "[]";
}

public class BugCorpusEntry
{
    public int Id { get; set; }
    public string RepoPath { get; set; } = "";
    public BugCategory Category { get; set; }
    public string BeforeSnippet { get; set; } = "";
    public string AfterSnippet { get; set; } = "";
    public string CommitSha { get; set; } = "";
    public string Message { get; set; } = "";
}

public class ReviewDojoContext : DbContext
{
    public ReviewDojoContext(DbContextOptions<ReviewDojoContext> o) : base(o) { }
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Diff> Diffs => Set<Diff>();
    public DbSet<ManifestEntry> ManifestEntries => Set<ManifestEntry>();
    public DbSet<FindingRecord> Findings => Set<FindingRecord>();
    public DbSet<ScoreRecord> Scores => Set<ScoreRecord>();
    public DbSet<BugCorpusEntry> Corpus => Set<BugCorpusEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Session>().Property(x => x.DifficultyTier).HasConversion<string>();
        b.Entity<Diff>().Property(x => x.Verdict).HasConversion<string>();
        b.Entity<ManifestEntry>().Property(x => x.Category).HasConversion<string>();
        b.Entity<ManifestEntry>().Property(x => x.Severity).HasConversion<string>();
        b.Entity<FindingRecord>().Property(x => x.Category).HasConversion<string>();
        b.Entity<BugCorpusEntry>().Property(x => x.Category).HasConversion<string>();
        b.Entity<Diff>().HasOne(x => x.Score).WithOne().HasForeignKey<ScoreRecord>(x => x.DiffId);
    }
}
