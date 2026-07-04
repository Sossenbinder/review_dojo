# Review Dojo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a .NET code-review training app that generates real-codebase diffs with a controlled number of seeded bugs, lets the user review them in a browser, then scores found/missed/false-positive findings and tracks skill over time.

**Architecture:** ASP.NET Core Web API + Blazor WASM client, EF Core/SQLite. A server-side two-step generator produces a legitimate change (full after-contents) then injects M bugs; diffs and manifest line numbers are computed by us (DiffPlex + anchor resolution), never trusted from the model. Manifests are physically absent from pre-submit DTOs and revealed only on submit.

**Tech Stack:** .NET 8, ASP.NET Core, Blazor WebAssembly, EF Core + SQLite, DiffPlex, LibGit2Sharp, xUnit, diff2html (JS), Anthropic HTTP API.

---

## File Structure

```
ReviewDojo.sln
src/
  ReviewDojo.Core/            # pure domain + scoring, no I/O
    Taxonomy.cs               # BugCategory, Severity, Verdict enums
    Domain.cs                 # record types shared across layers
    Scoring/
      FindingMatcher.cs       # finding <-> manifest matching
      ScoreCalculator.cs      # recall/precision/sev-weighted/FP/calibration
      SizeSampler.cs          # seeded log-normal diff-size draw
  ReviewDojo.Data/
    ReviewDojoContext.cs      # EF Core DbContext + entities
    Migrations/               # generated
  ReviewDojo.Generator/
    DiffBuilder.cs            # before/after contents -> unified diff + line map
    AnchorResolver.cs         # snippet -> authoritative line range in diff
    IAnthropicClient.cs       # abstraction (real + fake)
    AnthropicClient.cs        # HTTP impl, key from env
    DiffGenerator.cs          # two-step orchestration
    GeneratorModels.cs        # request/response records for the two steps
  ReviewDojo.Api/
    Program.cs                # DI + endpoints
    Dtos.cs                   # DiffDto (no manifest) vs RevealDto (manifest)
    Endpoints/                # route handlers
  ReviewDojo.Client/          # Blazor WASM
    Pages/Review.razor, Reveal.razor, Stats.razor, Home.razor
    Services/ApiClient.cs
    wwwroot/js/diffview.js     # diff2html interop
  ReviewDojo.Cli/
    Program.cs                # `dojo mine` + `dojo gen`
    CorpusMiner.cs
tests/
  ReviewDojo.Tests/
    fixtures/mini-repo/        # tiny fixture git repo for determinism harness
    ScoringTests.cs, DiffBuilderTests.cs, AnchorResolverTests.cs,
    SizeSamplerTests.cs, GeneratorDeterminismTests.cs, ApiGatingTests.cs
README.md
```

---

## Task 1: Solution scaffold + project wiring

**Files:**
- Create: `ReviewDojo.sln` and all `src/*` + `tests/*` project files

- [ ] **Step 1: Create solution and projects**

```bash
dotnet new sln -n ReviewDojo
dotnet new classlib  -n ReviewDojo.Core      -o src/ReviewDojo.Core
dotnet new classlib  -n ReviewDojo.Data      -o src/ReviewDojo.Data
dotnet new classlib  -n ReviewDojo.Generator -o src/ReviewDojo.Generator
dotnet new webapi    -n ReviewDojo.Api        -o src/ReviewDojo.Api --use-minimal-apis
dotnet new blazorwasm -n ReviewDojo.Client    -o src/ReviewDojo.Client
dotnet new console   -n ReviewDojo.Cli        -o src/ReviewDojo.Cli
dotnet new xunit     -n ReviewDojo.Tests      -o tests/ReviewDojo.Tests
dotnet sln add (find src tests -name '*.csproj')
```

- [ ] **Step 2: Wire project references + packages**

```bash
dotnet add src/ReviewDojo.Data       reference src/ReviewDojo.Core
dotnet add src/ReviewDojo.Generator  reference src/ReviewDojo.Core
dotnet add src/ReviewDojo.Api        reference src/ReviewDojo.Core src/ReviewDojo.Data src/ReviewDojo.Generator
dotnet add src/ReviewDojo.Cli        reference src/ReviewDojo.Core src/ReviewDojo.Data src/ReviewDojo.Generator
dotnet add tests/ReviewDojo.Tests    reference src/ReviewDojo.Core src/ReviewDojo.Data src/ReviewDojo.Generator src/ReviewDojo.Api

dotnet add src/ReviewDojo.Data       package Microsoft.EntityFrameworkCore.Sqlite
dotnet add src/ReviewDojo.Data       package Microsoft.EntityFrameworkCore.Design
dotnet add src/ReviewDojo.Generator  package DiffPlex
dotnet add src/ReviewDojo.Cli        package LibGit2Sharp
dotnet add src/ReviewDojo.Api        package Microsoft.EntityFrameworkCore.Sqlite
```

- [ ] **Step 3: Build to verify the graph compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "chore: scaffold ReviewDojo solution and project graph"
```

---

## Task 2: Taxonomy + domain records (Core)

**Files:**
- Create: `src/ReviewDojo.Core/Taxonomy.cs`, `src/ReviewDojo.Core/Domain.cs`

- [ ] **Step 1: Write the enums**

```csharp
namespace ReviewDojo.Core;

// Difficulty ascending. MVP seeds Mechanical..Contextual; all 5 exist in the enum.
public enum BugCategory { Mechanical, EdgeCase, Contextual, Abstraction, AgentTypical }

public enum Severity { Low, Medium, High, Critical }

public enum Verdict { Approve, RequestChanges }

public enum DifficultyTier { Easy, Medium, Hard }
```

- [ ] **Step 2: Write the shared domain records**

```csharp
namespace ReviewDojo.Core;

// A hidden ground-truth bug. Server-only; never leaves the API before submit.
public record ManifestBug(
    string FilePath, int LineStart, int LineEnd,
    BugCategory Category, Severity Severity, string Description);

// A reviewer's claim.
public record ReviewFinding(
    string FilePath, int Line, BugCategory Category, string? Comment);

public record MatchResult(
    ManifestBug? Bug, ReviewFinding? Finding, double Credit, bool CategoryMatched);
// Credit: 1.0 exact, 0.5 category mismatch within proximity, 0.0 unmatched.
```

- [ ] **Step 3: Build**

Run: `dotnet build src/ReviewDojo.Core`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(core): bug taxonomy and shared domain records"
```

---

## Task 3: Finding↔manifest matcher (Core, TDD)

**Files:**
- Create: `src/ReviewDojo.Core/Scoring/FindingMatcher.cs`
- Test: `tests/ReviewDojo.Tests/ScoringTests.cs`

Matching rule (from spec): a finding matches a bug when same file, `|finding.Line - bug range|` within a proximity window (default 3 lines), category exact = credit 1.0, category mismatch within window = 0.5. Greedy best-first by credit then distance; each bug and finding used at most once.

- [ ] **Step 1: Write failing tests**

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ScoringTests`
Expected: FAIL — `FindingMatcher` does not exist.

- [ ] **Step 3: Implement the matcher**

```csharp
namespace ReviewDojo.Core.Scoring;

public class FindingMatcher
{
    private readonly int _proximity;
    public FindingMatcher(int proximity = 3) => _proximity = proximity;

    private int Distance(ManifestBug b, ReviewFinding f)
    {
        if (f.Line < b.LineStart) return b.LineStart - f.Line;
        if (f.Line > b.LineEnd) return f.Line - b.LineEnd;
        return 0;
    }

    public List<MatchResult> Match(IEnumerable<ManifestBug> bugs, IEnumerable<ReviewFinding> findings)
    {
        var bugList = bugs.ToList();
        var findList = findings.ToList();

        // Candidate pairs within proximity + same file, ranked by credit desc then distance asc.
        var candidates =
            (from bi in Enumerable.Range(0, bugList.Count)
             from fi in Enumerable.Range(0, findList.Count)
             let b = bugList[bi]
             let f = findList[fi]
             where string.Equals(b.FilePath, f.FilePath, StringComparison.OrdinalIgnoreCase)
             let dist = Distance(b, f)
             where dist <= _proximity
             let catOk = b.Category == f.Category
             select new { bi, fi, dist, catOk, credit = catOk ? 1.0 : 0.5 })
            .OrderByDescending(x => x.credit).ThenBy(x => x.dist)
            .ToList();

        var results = new List<MatchResult>();
        var usedBugs = new HashSet<int>();
        var usedFinds = new HashSet<int>();
        foreach (var c in candidates)
        {
            if (usedBugs.Contains(c.bi) || usedFinds.Contains(c.fi)) continue;
            usedBugs.Add(c.bi); usedFinds.Add(c.fi);
            results.Add(new MatchResult(bugList[c.bi], findList[c.fi], c.credit, c.catOk));
        }
        for (int bi = 0; bi < bugList.Count; bi++)      // misses
            if (!usedBugs.Contains(bi)) results.Add(new MatchResult(bugList[bi], null, 0, false));
        for (int fi = 0; fi < findList.Count; fi++)     // false positives
            if (!usedFinds.Contains(fi)) results.Add(new MatchResult(null, findList[fi], 0, false));
        return results;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ScoringTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(core): finding-to-manifest matcher with proximity + half-credit"
```

---

## Task 4: Score calculator (Core, TDD)

**Files:**
- Create: `src/ReviewDojo.Core/Scoring/ScoreCalculator.cs`
- Test: append to `tests/ReviewDojo.Tests/ScoringTests.cs`

Metrics from `List<MatchResult>` + verdict + isClean + elapsed:
- recall = Σcredit / bugCount (1.0 if bugCount==0)
- precision = Σcredit / findingCount (1.0 if findingCount==0)
- severityWeightedRecall = Σ(credit×weight) / Σ(weight), weights Low1/Med2/High3/Critical5
- falsePositiveRate = FP / findingCount (0 if none)
- verdictCorrect: clean→Approve correct; seeded→RequestChanges correct
- All packed into a `ScoreCard` record.

- [ ] **Step 1: Write the record + failing tests**

```csharp
// in ScoringTests.cs
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
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter ScoringTests`
Expected: FAIL — `ScoreCalculator` / `ScoreCard` missing.

- [ ] **Step 3: Implement**

```csharp
namespace ReviewDojo.Core.Scoring;

public record ScoreCard(
    double Recall, double Precision, double SeverityWeightedRecall,
    double FalsePositiveRate, bool VerdictCorrect, long ElapsedMs,
    int Hits, int Misses, int FalsePositives);

public static class ScoreCalculator
{
    static double Weight(Severity s) => s switch
    { Severity.Low => 1, Severity.Medium => 2, Severity.High => 3, Severity.Critical => 5, _ => 1 };

    public static ScoreCard Compute(
        IReadOnlyList<MatchResult> matches, Verdict verdict, bool isClean, long elapsedMs)
    {
        var bugMatches   = matches.Where(m => m.Bug != null).ToList();
        var findMatches  = matches.Where(m => m.Finding != null).ToList();
        int bugCount     = bugMatches.Count;
        int findCount    = findMatches.Count;
        double creditSum = matches.Where(m => m.Bug != null && m.Finding != null).Sum(m => m.Credit);

        double recall    = bugCount == 0 ? 1.0 : creditSum / bugCount;
        double precision = findCount == 0 ? 1.0 : creditSum / findCount;

        double wTotal = bugMatches.Sum(m => Weight(m.Bug!.Severity));
        double wHit   = bugMatches.Where(m => m.Finding != null).Sum(m => m.Credit * Weight(m.Bug!.Severity));
        double sevRecall = wTotal == 0 ? 1.0 : wHit / wTotal;

        int fp = matches.Count(m => m.Bug == null && m.Finding != null);
        double fpRate = findCount == 0 ? 0.0 : (double)fp / findCount;

        bool verdictCorrect = isClean ? verdict == Verdict.Approve : verdict == Verdict.RequestChanges;
        int hits   = matches.Count(m => m.Bug != null && m.Finding != null);
        int misses = matches.Count(m => m.Bug != null && m.Finding == null);

        return new ScoreCard(recall, precision, sevRecall, fpRate, verdictCorrect, elapsedMs, hits, misses, fp);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter ScoringTests`
Expected: PASS (all scoring tests).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(core): score calculator with severity weighting and calibration"
```

---

## Task 5: Seeded log-normal size sampler (Core, TDD)

**Files:**
- Create: `src/ReviewDojo.Core/Scoring/SizeSampler.cs`
- Test: `tests/ReviewDojo.Tests/SizeSamplerTests.cs`

Log-normal, median ~150, tail to ~800, tier scales the median (Easy 80, Medium 150, Hard 300). Deterministic given seed. `median = exp(mu)` so `mu = ln(median)`; pick `sigma=0.6`; clamp to [10, 800].

- [ ] **Step 1: Failing tests**

```csharp
using ReviewDojo.Core;
using ReviewDojo.Core.Scoring;
using Xunit;

public class SizeSamplerTests
{
    [Fact]
    public void SameSeed_IsDeterministic()
    {
        var a = new SizeSampler(seed: 42);
        var b = new SizeSampler(seed: 42);
        Assert.Equal(a.Sample(DifficultyTier.Medium), b.Sample(DifficultyTier.Medium));
    }

    [Fact]
    public void Samples_StayWithinClamp()
    {
        var s = new SizeSampler(seed: 7);
        for (int i = 0; i < 500; i++)
        {
            int n = s.Sample(DifficultyTier.Hard);
            Assert.InRange(n, 10, 800);
        }
    }

    [Fact]
    public void MedianTier_CentersNear150()
    {
        var s = new SizeSampler(seed: 1);
        var vals = Enumerable.Range(0, 2000).Select(_ => s.Sample(DifficultyTier.Medium)).OrderBy(x => x).ToList();
        int median = vals[vals.Count / 2];
        Assert.InRange(median, 110, 200);
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter SizeSamplerTests`
Expected: FAIL — `SizeSampler` missing.

- [ ] **Step 3: Implement (Box-Muller on seeded Random)**

```csharp
namespace ReviewDojo.Core.Scoring;

public class SizeSampler
{
    private readonly Random _rng;
    public SizeSampler(int seed) => _rng = new Random(seed);

    static double Median(DifficultyTier t) => t switch
    { DifficultyTier.Easy => 80, DifficultyTier.Medium => 150, DifficultyTier.Hard => 300, _ => 150 };

    public int Sample(DifficultyTier tier)
    {
        // Standard normal via Box-Muller, then exp(mu + sigma*z).
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        double mu = Math.Log(Median(tier));
        double val = Math.Exp(mu + 0.6 * z);
        return Math.Clamp((int)Math.Round(val), 10, 800);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter SizeSamplerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(core): seeded log-normal diff-size sampler"
```

---

## Task 6: EF Core context + entities + migration (Data)

**Files:**
- Create: `src/ReviewDojo.Data/ReviewDojoContext.cs`
- Test: `tests/ReviewDojo.Tests/DataSmokeTests.cs`

Entities mirror the spec data model. Enums stored as strings. `ManifestEntry` lives in the same DB but is only ever read by server code.

- [ ] **Step 1: Write entities + context**

```csharp
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
        foreach (var et in new[] {
            typeof(Session), typeof(Diff), typeof(ManifestEntry),
            typeof(FindingRecord), typeof(ScoreRecord), typeof(BugCorpusEntry) })
        { /* default mapping */ }
        b.Entity<Session>().Property(x => x.DifficultyTier).HasConversion<string>();
        b.Entity<Diff>().Property(x => x.Verdict).HasConversion<string>();
        b.Entity<ManifestEntry>().Property(x => x.Category).HasConversion<string>();
        b.Entity<ManifestEntry>().Property(x => x.Severity).HasConversion<string>();
        b.Entity<FindingRecord>().Property(x => x.Category).HasConversion<string>();
        b.Entity<BugCorpusEntry>().Property(x => x.Category).HasConversion<string>();
        b.Entity<Diff>().HasOne(x => x.Score).WithOne().HasForeignKey<ScoreRecord>(x => x.DiffId);
    }
}
```

- [ ] **Step 2: Create the initial migration**

```bash
dotnet tool install --global dotnet-ef   # if not present
dotnet ef migrations add InitialCreate \
  --project src/ReviewDojo.Data --startup-project src/ReviewDojo.Api \
  --output-dir Migrations
```

Add to `src/ReviewDojo.Api/Program.cs` DI (needed for the ef design-time factory):
```csharp
builder.Services.AddDbContext<ReviewDojoContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "Data Source=reviewdojo.db"));
```

- [ ] **Step 3: Write a data smoke test (in-memory SQLite)**

```csharp
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
```

Add `Microsoft.Data.Sqlite` to the test project: `dotnet add tests/ReviewDojo.Tests package Microsoft.Data.Sqlite`.

- [ ] **Step 4: Run**

Run: `dotnet test --filter DataSmokeTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(data): EF Core context, entities, initial migration"
```

---

## Task 7: DiffBuilder — before/after → unified diff + line map (Generator, TDD)

**Files:**
- Create: `src/ReviewDojo.Generator/DiffBuilder.cs`
- Test: `tests/ReviewDojo.Tests/DiffBuilderTests.cs`

We own line numbers: given per-file (path, beforeText, afterText), produce a git-style unified diff string and a `DiffLineMap` that, for each file, knows the new-file line number of every added/context line. Uses DiffPlex `InlineDiffBuilder`.

- [ ] **Step 1: Failing tests**

```csharp
using ReviewDojo.Generator;
using Xunit;

public class DiffBuilderTests
{
    [Fact]
    public void ProducesUnifiedDiffWithHunkHeader()
    {
        var b = new DiffBuilder();
        var d = b.Build(new[] { new FileChange("a.cs", "line1\nline2\n", "line1\nCHANGED\n") });
        Assert.Contains("--- a/a.cs", d.UnifiedText);
        Assert.Contains("+++ b/a.cs", d.UnifiedText);
        Assert.Contains("+CHANGED", d.UnifiedText);
        Assert.Contains("-line2", d.UnifiedText);
    }

    [Fact]
    public void LineMap_FindsNewLineNumberOfAddedContent()
    {
        var b = new DiffBuilder();
        var d = b.Build(new[] { new FileChange("a.cs", "x\ny\nz\n", "x\ny\nNEW\nz\n") });
        int line = d.NewLineOf("a.cs", "NEW");
        Assert.Equal(3, line);  // NEW is the 3rd line of the after-file
    }

    [Fact]
    public void ChangedLineCount_CountsAddsAndRemoves()
    {
        var b = new DiffBuilder();
        var d = b.Build(new[] { new FileChange("a.cs", "a\nb\n", "a\nB\nc\n") });
        Assert.True(d.ChangedLineCount >= 2);
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter DiffBuilderTests`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement**

```csharp
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.Text;

namespace ReviewDojo.Generator;

public record FileChange(string Path, string Before, string After);

public class BuiltDiff
{
    public string UnifiedText { get; init; } = "";
    public int ChangedLineCount { get; init; }
    // path -> list of (newLineNumber, text) for lines present in the after-file (context + added)
    public Dictionary<string, List<(int Line, string Text)>> NewLines { get; init; } = new();

    // First new-file line number whose text contains the anchor snippet (trimmed).
    public int NewLineOf(string path, string anchor)
    {
        var needle = anchor.Trim();
        if (!NewLines.TryGetValue(path, out var lines)) return -1;
        var hit = lines.FirstOrDefault(l => l.Text.Contains(needle));
        return hit == default ? -1 : hit.Line;
    }
}

public class DiffBuilder
{
    public BuiltDiff Build(IEnumerable<FileChange> changes)
    {
        var sb = new StringBuilder();
        int changed = 0;
        var newLines = new Dictionary<string, List<(int, string)>>();

        foreach (var ch in changes)
        {
            var model = InlineDiffBuilder.Diff(ch.Before, ch.After);
            sb.AppendLine($"--- a/{ch.Path}");
            sb.AppendLine($"+++ b/{ch.Path}");
            int newLineNo = 0;
            var perFile = new List<(int, string)>();
            foreach (var line in model.Lines)
            {
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        newLineNo++; changed++;
                        sb.AppendLine("+" + line.Text);
                        perFile.Add((newLineNo, line.Text));
                        break;
                    case ChangeType.Deleted:
                        changed++;
                        sb.AppendLine("-" + line.Text);
                        break;
                    case ChangeType.Unchanged:
                        newLineNo++;
                        sb.AppendLine(" " + line.Text);
                        perFile.Add((newLineNo, line.Text));
                        break;
                    default:
                        newLineNo++;
                        sb.AppendLine(" " + line.Text);
                        perFile.Add((newLineNo, line.Text));
                        break;
                }
            }
            newLines[ch.Path] = perFile;
        }
        return new BuiltDiff { UnifiedText = sb.ToString(), ChangedLineCount = changed, NewLines = newLines };
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter DiffBuilderTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(generator): DiffBuilder produces unified diff + new-line map"
```

---

## Task 8: AnchorResolver — snippet → authoritative line range (Generator, TDD)

**Files:**
- Create: `src/ReviewDojo.Generator/AnchorResolver.cs`
- Test: `tests/ReviewDojo.Tests/AnchorResolverTests.cs`

Given a `BuiltDiff` and a raw manifest entry (file + anchor snippet + category/severity/description), resolve to a `ManifestBug` with real line numbers. If the anchor cannot be located in the produced diff, the entry is **dropped** (returns null) — this is what keeps the determinism harness honest: no phantom manifest lines.

- [ ] **Step 1: Failing tests**

```csharp
using ReviewDojo.Core;
using ReviewDojo.Generator;
using Xunit;

public class AnchorResolverTests
{
    static BuiltDiff Diff() => new DiffBuilder().Build(new[] {
        new FileChange("a.cs", "int x=0;\n", "int x=0;\nif (n = 5) {}\n") });

    [Fact]
    public void ResolvesAnchorToNewLineRange()
    {
        var r = new AnchorResolver();
        var raw = new RawManifestEntry("a.cs", "if (n = 5)", BugCategory.Mechanical, Severity.High, "assignment not comparison");
        var bug = r.Resolve(Diff(), raw);
        Assert.NotNull(bug);
        Assert.Equal(2, bug!.LineStart);
        Assert.Equal(2, bug.LineEnd);
        Assert.Equal(BugCategory.Mechanical, bug.Category);
    }

    [Fact]
    public void UnlocatableAnchor_IsDropped()
    {
        var r = new AnchorResolver();
        var raw = new RawManifestEntry("a.cs", "this text is not in the diff", BugCategory.Mechanical, Severity.Low, "d");
        Assert.Null(r.Resolve(Diff(), raw));
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter AnchorResolverTests`
Expected: FAIL — types missing.

- [ ] **Step 3: Implement**

```csharp
using ReviewDojo.Core;

namespace ReviewDojo.Generator;

public record RawManifestEntry(
    string FilePath, string Anchor, BugCategory Category, Severity Severity, string Description);

public class AnchorResolver
{
    // Returns a ManifestBug with real line numbers, or null if the anchor
    // isn't present in the produced diff (drop phantom entries).
    public ManifestBug? Resolve(BuiltDiff diff, RawManifestEntry raw)
    {
        int line = diff.NewLineOf(raw.FilePath, raw.Anchor);
        if (line < 0) return null;

        // If the anchor spans multiple lines, widen the range to cover them.
        var anchorLines = raw.Anchor.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        int end = line + Math.Max(0, anchorLines - 1);
        return new ManifestBug(raw.FilePath, line, end, raw.Category, raw.Severity, raw.Description);
    }

    public List<ManifestBug> ResolveAll(BuiltDiff diff, IEnumerable<RawManifestEntry> raws)
        => raws.Select(r => Resolve(diff, r)).Where(b => b != null).Select(b => b!).ToList();
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter AnchorResolverTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(generator): anchor resolver maps snippets to real diff lines, drops phantoms"
```

---

## Task 9: Anthropic client abstraction + fake (Generator)

**Files:**
- Create: `src/ReviewDojo.Generator/IAnthropicClient.cs`, `AnthropicClient.cs`, `GeneratorModels.cs`

Abstraction so the generator is testable with a fake; real impl calls the HTTP API with the key from `ANTHROPIC_API_KEY`. Model id configurable (default `claude-sonnet-4-6`).

- [ ] **Step 1: Define request/response models + interface**

```csharp
namespace ReviewDojo.Generator;

public record LlmMessage(string Role, string Content);

public record LlmRequest(string Model, string System, IReadOnlyList<LlmMessage> Messages, int MaxTokens = 8000);

public interface IAnthropicClient
{
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}
```

- [ ] **Step 2: Implement the HTTP client (env key, never logged)**

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace ReviewDojo.Generator;

public class AnthropicClient : IAnthropicClient
{
    private readonly HttpClient _http;
    public AnthropicClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.anthropic.com/");
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Add("x-api-key", key);
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            model = request.Model,
            max_tokens = request.MaxTokens,
            system = request.System,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };
        using var resp = await _http.PostAsJsonAsync("v1/messages", body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        // content: [{type:"text", text:"..."}]
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }
}
```

> NOTE FOR IMPLEMENTER: Before finalizing the request/response shape, invoke the `claude-api` skill to confirm the current `/v1/messages` schema, headers, and the default model id `claude-sonnet-4-6`. Do not answer from memory.

- [ ] **Step 3: Build (no unit test — thin I/O adapter, covered via fake in Task 11)**

Run: `dotnet build src/ReviewDojo.Generator`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(generator): Anthropic client abstraction + HTTP impl (env key)"
```

---

## Task 10: DiffGenerator two-step orchestration (Generator, TDD with fake)

**Files:**
- Create: `src/ReviewDojo.Generator/DiffGenerator.cs`
- Test: `tests/ReviewDojo.Tests/GeneratorDeterminismTests.cs` (fixture repo + fake client)

Flow: read locus files from repo (read-only) → Step 1 call returns JSON `{files:[{path,after}]}` → build clean diff → decide M from tier+cleanRate+seed → if M>0, Step 2 call returns JSON `{files:[{path,after}], bugs:[{path,anchor,category,severity,description}]}` → rebuild diff → anchor-resolve manifest. Returns `GeneratedDiff(UnifiedText, IsClean, ChangedLineCount, List<ManifestBug>)`.

- [ ] **Step 1: Define output + a JSON contract, write fixture + failing determinism test**

Create fixture: `tests/ReviewDojo.Tests/fixtures/mini-repo/Calc.cs` with a few methods; a small helper `FakeAnthropicClient` that returns canned JSON keyed by which step (detect via system prompt marker), producing a known bug at a known anchor, and a clean variant.

```csharp
using ReviewDojo.Core;
using ReviewDojo.Generator;
using Xunit;

public class GeneratorDeterminismTests
{
    // Fake returns: step1 -> after-contents adding a helper; step2 -> injects "if (n = 0)".
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
        for (int i = 0; i < 10; i++)
        {
            // even seeds -> force clean, odd -> force one bug (cleanRate wired via seed in Decide)
            bool forceClean = i % 2 == 0;
            var res = await gen.GenerateAsync(
                repoPath: "fixtures/mini-repo",
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
                foreach (var bug in res.Manifest)
                {
                    // the referenced line text must appear as an added/context line in the diff
                    Assert.Contains($"if (n = 0)", res.UnifiedText);
                    Assert.InRange(bug.LineStart, 1, 9999);
                }
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter GeneratorDeterminismTests`
Expected: FAIL — `DiffGenerator` missing.

- [ ] **Step 3: Implement the orchestrator**

```csharp
using System.Text.Json;
using ReviewDojo.Core;

namespace ReviewDojo.Generator;

public record GeneratedDiff(string UnifiedText, bool IsClean, int ChangedLineCount, List<ManifestBug> Manifest);

public class DiffGenerator
{
    private readonly IAnthropicClient _client;
    private readonly string _model;
    private readonly DiffBuilder _diffBuilder = new();
    private readonly AnchorResolver _resolver = new();

    public DiffGenerator(IAnthropicClient client, string model) { _client = client; _model = model; }

    public async Task<GeneratedDiff> GenerateAsync(
        string repoPath, DifficultyTier tier, int seed, int? forceMistakeCount = null,
        IReadOnlyList<BugFewShot>? fewShots = null, CancellationToken ct = default)
    {
        // 1. Select locus (read-only). Pick source files deterministically by seed.
        var files = LocusSelector.Select(repoPath, tier, seed);
        var before = files.ToDictionary(f => f.RelPath, f => f.Text);

        // 2. Step 1: legitimate change.
        var step1Json = await _client.CompleteAsync(new LlmRequest(
            _model, PromptLibrary.LegitChangeSystem,
            new[] { new LlmMessage("user", PromptLibrary.LegitChangeUser(before, tier)) }), ct);
        var afterClean = ParseFiles(step1Json);
        var cleanChanges = afterClean.Select(kv => new FileChange(kv.Key, before.GetValueOrDefault(kv.Key, ""), kv.Value)).ToList();

        // 3. Decide M.
        int m = forceMistakeCount ?? MistakeCountPolicy.Decide(tier, seed, cleanRate: 0.2);
        if (m == 0)
        {
            var cleanDiff = _diffBuilder.Build(cleanChanges);
            return new GeneratedDiff(cleanDiff.UnifiedText, true, cleanDiff.ChangedLineCount, new());
        }

        // 4. Step 2: inject M mistakes on top of the clean after-contents.
        var step2Json = await _client.CompleteAsync(new LlmRequest(
            _model, PromptLibrary.InjectSystem,   // contains "INJECT" marker
            new[] { new LlmMessage("user", PromptLibrary.InjectUser(afterClean, m, tier, fewShots)) }), ct);
        var (afterBuggy, rawBugs) = ParseFilesAndBugs(step2Json);

        var buggyChanges = afterBuggy.Select(kv => new FileChange(kv.Key, before.GetValueOrDefault(kv.Key, ""), kv.Value)).ToList();
        var builtDiff = _diffBuilder.Build(buggyChanges);
        var manifest = _resolver.ResolveAll(builtDiff, rawBugs);

        // If every anchor failed to resolve, treat as clean rather than lie.
        return new GeneratedDiff(builtDiff.UnifiedText, manifest.Count == 0, builtDiff.ChangedLineCount, manifest);
    }

    static Dictionary<string, string> ParseFiles(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("files").EnumerateArray()
            .ToDictionary(e => e.GetProperty("path").GetString()!, e => e.GetProperty("after").GetString()!);
    }

    static (Dictionary<string,string>, List<RawManifestEntry>) ParseFilesAndBugs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("files").EnumerateArray()
            .ToDictionary(e => e.GetProperty("path").GetString()!, e => e.GetProperty("after").GetString()!);
        var bugs = new List<RawManifestEntry>();
        if (doc.RootElement.TryGetProperty("bugs", out var arr))
            foreach (var e in arr.EnumerateArray())
                bugs.Add(new RawManifestEntry(
                    e.GetProperty("path").GetString()!,
                    e.GetProperty("anchor").GetString()!,
                    Enum.Parse<BugCategory>(e.GetProperty("category").GetString()!, true),
                    Enum.Parse<Severity>(e.GetProperty("severity").GetString()!, true),
                    e.GetProperty("description").GetString()!));
        return (files, bugs);
    }
}
```

- [ ] **Step 4: Add the supporting helpers (`LocusSelector`, `MistakeCountPolicy`, `PromptLibrary`, `BugFewShot`)**

```csharp
using ReviewDojo.Core;
namespace ReviewDojo.Generator;

public record LocusFile(string RelPath, string Text);
public record BugFewShot(BugCategory Category, string Before, string After, string Message);

public static class LocusSelector
{
    static readonly string[] Exts = { ".cs", ".ts", ".js", ".py", ".go", ".java", ".rb" };
    // Deterministic: order candidate files, pick a seed-selected window. Read-only.
    public static List<LocusFile> Select(string repoPath, DifficultyTier tier, int seed)
    {
        var all = Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
            .Where(p => Exts.Contains(Path.GetExtension(p)))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
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
    // Returns 0 with probability cleanRate; otherwise 1..K scaled by tier. Deterministic by seed.
    public static int Decide(DifficultyTier tier, int seed, double cleanRate)
    {
        var rng = new Random(seed);
        if (rng.NextDouble() < cleanRate) return 0;
        int k = tier switch { DifficultyTier.Easy => 1, DifficultyTier.Medium => 2, DifficultyTier.Hard => 3, _ => 1 };
        return 1 + rng.Next(k);   // 1..k
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
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test --filter GeneratorDeterminismTests`
Expected: PASS. (This IS the determinism harness required by the spec: 10× fixture runs, every manifest entry maps to real diff lines, clean runs empty.)

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(generator): two-step diff generator + determinism harness"
```

---

## Task 11: API DTOs with manifest-gating (Api, TDD)

**Files:**
- Create: `src/ReviewDojo.Api/Dtos.cs`
- Test: `tests/ReviewDojo.Tests/ApiGatingTests.cs`

The **type-level guarantee**: `DiffDto` has no manifest field at all; only `RevealDto` does. A compile-time-plus-reflection test asserts `DiffDto` exposes nothing named Manifest/Bug.

- [ ] **Step 1: Define DTOs**

```csharp
using ReviewDojo.Core;
namespace ReviewDojo.Api;

// Pre-submit. Physically cannot carry ground truth.
public record DiffDto(int Id, int Ordinal, int SizeLines, string UnifiedDiffText,
    DateTime? StartedAt, DateTime? SubmittedAt);

public record FindingDto(string FilePath, int Line, BugCategory Category, string? Comment);

public record SubmitRequest(Verdict Verdict, IReadOnlyList<FindingDto> Findings);

// Post-submit only.
public record ManifestBugDto(string FilePath, int LineStart, int LineEnd,
    BugCategory Category, Severity Severity, string Description);

public record RevealDto(int DiffId, IReadOnlyList<ManifestBugDto> Manifest, ScoreDto Score,
    IReadOnlyList<MatchDto> Matches);

public record ScoreDto(double Recall, double Precision, double SeverityWeightedRecall,
    double FalsePositiveRate, bool VerdictCorrect, long TimeMs, int Hits, int Misses, int FalsePositives);

public record MatchDto(string? BugFile, int? BugLine, string? FindingFile, int? FindingLine, double Credit);
```

- [ ] **Step 2: Failing reflection test**

```csharp
using System.Reflection;
using ReviewDojo.Api;
using Xunit;

public class ApiGatingTests
{
    [Fact]
    public void DiffDto_HasNoGroundTruthMembers()
    {
        var props = typeof(DiffDto).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(props, p =>
            p.Name.Contains("Manifest", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Bug", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Severity", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 3: Run — it should pass immediately once DTOs compile**

Run: `dotnet test --filter ApiGatingTests`
Expected: PASS (guards against future regression where someone adds a manifest field to DiffDto).

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(api): pre/post-submit DTO split enforcing manifest gating"
```

---

## Task 12: API endpoints + submit scoring (Api)

**Files:**
- Modify: `src/ReviewDojo.Api/Program.cs`
- Create: `src/ReviewDojo.Api/Endpoints/DojoEndpoints.cs`
- Test: `tests/ReviewDojo.Tests/ApiEndpointTests.cs` (WebApplicationFactory)

Endpoints: `POST /sessions`, `POST /sessions/{id}/diffs/next`, `GET /diffs/{id}`, `POST /diffs/{id}/start`, `POST /diffs/{id}/submit`, `GET /diffs/{id}/reveal`, `GET /stats`. `reveal` returns 403 until submitted. `submit` runs `FindingMatcher` + `ScoreCalculator`, persists `ScoreRecord`, returns `RevealDto`.

- [ ] **Step 1: Implement endpoint mapping**

```csharp
using Microsoft.EntityFrameworkCore;
using ReviewDojo.Core;
using ReviewDojo.Core.Scoring;
using ReviewDojo.Data;
using ReviewDojo.Generator;
using System.Text.Json;

namespace ReviewDojo.Api;

public static class DojoEndpoints
{
    public static void MapDojo(this WebApplication app)
    {
        app.MapPost("/sessions", async (CreateSessionRequest req, ReviewDojoContext db) =>
        {
            var s = new Session {
                TargetRepoPath = req.TargetRepoPath, DifficultyTier = req.Tier,
                CleanRate = req.CleanRate ?? 0.2, Seed = req.Seed ?? Environment.TickCount,
                CreatedAt = DateTime.UtcNow };
            db.Sessions.Add(s); await db.SaveChangesAsync();
            return Results.Ok(new { s.Id });
        });

        app.MapPost("/sessions/{id:int}/diffs/next", async (int id, ReviewDojoContext db, DiffGenerator gen) =>
        {
            var s = await db.Sessions.Include(x => x.Diffs).FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            int ordinal = s.Diffs.Count;
            int diffSeed = HashCode.Combine(s.Seed, ordinal);
            var g = await gen.GenerateAsync(s.TargetRepoPath, s.DifficultyTier, diffSeed);
            var diff = new Diff {
                SessionId = id, Ordinal = ordinal, UnifiedDiffText = g.UnifiedText,
                IsClean = g.IsClean, SizeLines = g.ChangedLineCount, Seed = diffSeed,
                GeneratedAt = DateTime.UtcNow,
                Manifest = g.Manifest.Select(b => new ManifestEntry {
                    FilePath = b.FilePath, LineStart = b.LineStart, LineEnd = b.LineEnd,
                    Category = b.Category, Severity = b.Severity, Description = b.Description }).ToList() };
            db.Diffs.Add(diff); await db.SaveChangesAsync();
            return Results.Ok(ToDiffDto(diff));   // NO manifest
        });

        app.MapGet("/diffs/{id:int}", async (int id, ReviewDojoContext db) =>
        {
            var d = await db.Diffs.FindAsync(id);
            return d is null ? Results.NotFound() : Results.Ok(ToDiffDto(d));
        });

        app.MapPost("/diffs/{id:int}/start", async (int id, ReviewDojoContext db) =>
        {
            var d = await db.Diffs.FindAsync(id);
            if (d is null) return Results.NotFound();
            d.StartedAt ??= DateTime.UtcNow; await db.SaveChangesAsync();
            return Results.Ok();
        });

        app.MapPost("/diffs/{id:int}/submit", async (int id, SubmitRequest req, ReviewDojoContext db) =>
        {
            var d = await db.Diffs.Include(x => x.Manifest).Include(x => x.Findings)
                                  .FirstOrDefaultAsync(x => x.Id == id);
            if (d is null) return Results.NotFound();
            if (d.SubmittedAt != null) return Results.Conflict("Already submitted.");

            var started = d.StartedAt ?? d.GeneratedAt;
            d.SubmittedAt = DateTime.UtcNow;
            d.Verdict = req.Verdict;
            d.Findings = req.Findings.Select(f => new FindingRecord {
                FilePath = f.FilePath, Line = f.Line, Category = f.Category,
                Comment = f.Comment, CreatedAt = DateTime.UtcNow }).ToList();

            var bugs = d.Manifest.Select(m => new ManifestBug(m.FilePath, m.LineStart, m.LineEnd, m.Category, m.Severity, m.Description)).ToList();
            var finds = d.Findings.Select(f => new ReviewFinding(f.FilePath, f.Line, f.Category, f.Comment)).ToList();
            var matches = new FindingMatcher().Match(bugs, finds);
            long elapsed = (long)(d.SubmittedAt.Value - started).TotalMilliseconds;
            var card = ScoreCalculator.Compute(matches, req.Verdict, d.IsClean, elapsed);

            d.Score = new ScoreRecord {
                Recall = card.Recall, Precision = card.Precision,
                SeverityWeightedRecall = card.SeverityWeightedRecall,
                FalsePositiveRate = card.FalsePositiveRate, VerdictCorrect = card.VerdictCorrect,
                TimeMs = card.ElapsedMs,
                MatchesJson = JsonSerializer.Serialize(matches.Select(m => new MatchDto(
                    m.Bug?.FilePath, m.Bug?.LineStart, m.Finding?.FilePath, m.Finding?.Line, m.Credit))) };
            await db.SaveChangesAsync();
            return Results.Ok(ToReveal(d, card, matches));
        });

        app.MapGet("/diffs/{id:int}/reveal", async (int id, ReviewDojoContext db) =>
        {
            var d = await db.Diffs.Include(x => x.Manifest).Include(x => x.Score)
                                  .FirstOrDefaultAsync(x => x.Id == id);
            if (d is null) return Results.NotFound();
            if (d.SubmittedAt is null) return Results.StatusCode(403);   // gate
            var bugs = d.Manifest.Select(m => new ManifestBug(m.FilePath, m.LineStart, m.LineEnd, m.Category, m.Severity, m.Description)).ToList();
            var finds = new List<ReviewFinding>();
            var matches = new FindingMatcher().Match(bugs, finds);
            var card = new ScoreCard(d.Score!.Recall, d.Score.Precision, d.Score.SeverityWeightedRecall,
                d.Score.FalsePositiveRate, d.Score.VerdictCorrect, d.Score.TimeMs, 0, 0, 0);
            return Results.Ok(ToReveal(d, card, matches));
        });

        app.MapGet("/stats", async (ReviewDojoContext db) =>
        {
            var scored = await db.Diffs.Include(x => x.Score).Include(x => x.Manifest)
                .Where(x => x.Score != null).ToListAsync();
            return Results.Ok(StatsBuilder.Build(scored));
        });
    }

    static DiffDto ToDiffDto(Diff d) =>
        new(d.Id, d.Ordinal, d.SizeLines, d.UnifiedDiffText, d.StartedAt, d.SubmittedAt);

    static RevealDto ToReveal(Diff d, ScoreCard card, IReadOnlyList<MatchResult> matches) =>
        new(d.Id,
            d.Manifest.Select(m => new ManifestBugDto(m.FilePath, m.LineStart, m.LineEnd, m.Category, m.Severity, m.Description)).ToList(),
            new ScoreDto(card.Recall, card.Precision, card.SeverityWeightedRecall, card.FalsePositiveRate,
                card.VerdictCorrect, card.ElapsedMs, card.Hits, card.Misses, card.FalsePositives),
            matches.Select(m => new MatchDto(m.Bug?.FilePath, m.Bug?.LineStart, m.Finding?.FilePath, m.Finding?.Line, m.Credit)).ToList());
}

public record CreateSessionRequest(string TargetRepoPath, DifficultyTier Tier, double? CleanRate, int? Seed);
```

- [ ] **Step 2: Wire Program.cs (DI, CORS for WASM, generator, migrate on boot)**

```csharp
using Microsoft.EntityFrameworkCore;
using ReviewDojo.Api;
using ReviewDojo.Data;
using ReviewDojo.Generator;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ReviewDojoContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Db") ?? "Data Source=reviewdojo.db"));
builder.Services.AddHttpClient<IAnthropicClient, AnthropicClient>();
builder.Services.AddScoped(sp => new DiffGenerator(
    sp.GetRequiredService<IAnthropicClient>(),
    builder.Configuration["Anthropic:Model"] ?? "claude-sonnet-4-6"));
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(builder.Configuration["ClientOrigin"] ?? "https://localhost:7002")
     .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<ReviewDojoContext>().Database.Migrate();
app.UseCors();
app.MapDojo();
app.Run();

public partial class Program { }   // for WebApplicationFactory
```

- [ ] **Step 3: Endpoint test — reveal is gated, submit scores**

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class ApiEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _f;
    public ApiEndpointTests(WebApplicationFactory<Program> f) => _f = f;

    [Fact]
    public async Task Reveal_BeforeSubmit_Is403()
    {
        // Requires a seeded diff row; use a test DB with one un-submitted diff.
        var client = _f.CreateClient();
        var resp = await client.GetAsync("/diffs/999999/reveal");
        Assert.True(resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }
}
```

> NOTE: full submit-flow integration needs a test DB with a pre-seeded diff (no live API). Use a custom `WebApplicationFactory` that swaps the connection string to a temp SQLite file and seeds one diff + manifest directly via the context before asserting `submit` returns a populated `RevealDto` and `reveal` then returns 200.

- [ ] **Step 4: Run**

Run: `dotnet test --filter ApiEndpointTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): session/diff/submit/reveal/stats endpoints with gated reveal"
```

---

## Task 13: Stats aggregation (Api/Core, TDD)

**Files:**
- Create: `src/ReviewDojo.Api/StatsBuilder.cs`
- Test: `tests/ReviewDojo.Tests/StatsTests.cs`

Aggregate scored diffs into: overall recall/precision/FP-rate, median time, per-category recall (hits vs manifest count by category), calibration (approval rate on clean vs rejection rate on seeded), and a time-ordered trend series.

- [ ] **Step 1: Failing test**

```csharp
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
            IsClean = false, Verdict = Verdict.RequestChanges,
            Manifest = {
                new ManifestEntry { Category = BugCategory.Mechanical, Severity = Severity.High },
                new ManifestEntry { Category = BugCategory.EdgeCase, Severity = Severity.Low } },
            Score = new ScoreRecord {
                Recall = 0.5, Precision = 1, VerdictCorrect = true, TimeMs = 1000,
                MatchesJson = "[{\"BugFile\":\"a.cs\",\"BugLine\":1,\"FindingFile\":\"a.cs\",\"FindingLine\":1,\"Credit\":1.0}]" } };
        var stats = StatsBuilder.Build(new[] { diff });
        Assert.Equal(1, stats.TotalScored);
        Assert.True(stats.PerCategoryRecall.ContainsKey(nameof(BugCategory.Mechanical)));
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter StatsTests`
Expected: FAIL — `StatsBuilder` missing.

- [ ] **Step 3: Implement**

```csharp
using ReviewDojo.Core;
using ReviewDojo.Data;
namespace ReviewDojo.Api;

public record StatsDto(
    int TotalScored, double MeanRecall, double MeanPrecision, double MeanFpRate,
    double MedianTimeMs, Dictionary<string,double> PerCategoryRecall,
    double CleanApprovalRate, double SeededRejectionRate, List<double> RecallTrend);

public static class StatsBuilder
{
    public static StatsDto Build(IReadOnlyCollection<Diff> scored)
    {
        if (scored.Count == 0)
            return new(0,0,0,0,0,new(),0,0,new());

        double MeanOf(Func<Diff,double> f) => scored.Average(f);
        var times = scored.Select(d => (double)d.Score!.TimeMs).OrderBy(x => x).ToList();
        double median = times[times.Count / 2];

        // Per-category recall: for each manifest bug of category C, was it hit? Approximate
        // hit as: a match row with non-null FindingFile within the same score's matches.
        var perCat = new Dictionary<string,(double hit,double total)>();
        foreach (var d in scored)
        {
            var hitCount = System.Text.Json.JsonSerializer
                .Deserialize<List<MatchDto>>(d.Score!.MatchesJson)!
                .Count(m => m.BugFile != null && m.FindingFile != null);
            // Distribute hits across categories proportionally by manifest composition.
            var byCat = d.Manifest.GroupBy(m => m.Category.ToString());
            int manifestTotal = d.Manifest.Count;
            foreach (var g in byCat)
            {
                double share = manifestTotal == 0 ? 0 : (double)g.Count() / manifestTotal * hitCount;
                var cur = perCat.GetValueOrDefault(g.Key, (0,0));
                perCat[g.Key] = (cur.hit + share, cur.total + g.Count());
            }
        }
        var perCatRecall = perCat.ToDictionary(kv => kv.Key,
            kv => kv.Value.total == 0 ? 0 : kv.Value.hit / kv.Value.total);

        var clean = scored.Where(d => d.IsClean).ToList();
        var seeded = scored.Where(d => !d.IsClean).ToList();
        double cleanApproval = clean.Count == 0 ? 0 : (double)clean.Count(d => d.Verdict == Verdict.Approve) / clean.Count;
        double seededReject  = seeded.Count == 0 ? 0 : (double)seeded.Count(d => d.Verdict == Verdict.RequestChanges) / seeded.Count;

        var trend = scored.OrderBy(d => d.SubmittedAt).Select(d => d.Score!.Recall).ToList();

        return new(scored.Count, MeanOf(d => d.Score!.Recall), MeanOf(d => d.Score!.Precision),
            MeanOf(d => d.Score!.FalsePositiveRate), median, perCatRecall,
            cleanApproval, seededReject, trend);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter StatsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api): stats aggregation with per-category recall and calibration"
```

---

## Task 14: Blazor client — API service + diff2html interop

**Files:**
- Create: `src/ReviewDojo.Client/Services/ApiClient.cs`, `wwwroot/js/diffview.js`
- Modify: `src/ReviewDojo.Client/Program.cs`, `wwwroot/index.html`

- [ ] **Step 1: Add diff2html assets to `index.html` `<head>`**

```html
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/diff2html/bundles/css/diff2html.min.css" />
<script src="https://cdn.jsdelivr.net/npm/diff2html/bundles/js/diff2html-ui.min.js"></script>
<script src="js/diffview.js"></script>
```

- [ ] **Step 2: Interop JS — render unified/side-by-side + overlay**

```javascript
// wwwroot/js/diffview.js
window.dojoDiff = {
  render: function (elementId, diffText, mode) {
    const target = document.getElementById(elementId);
    if (!target) return;
    const ui = new Diff2HtmlUI(target, diffText, {
      drawFileList: false,
      matching: 'lines',
      outputFormat: mode === 'side' ? 'side-by-side' : 'line-by-line'
    });
    ui.draw();
  },
  // Adds a colored marker to a rendered line (used on the reveal screen).
  markLine: function (elementId, file, line, cssClass) {
    const rows = document.querySelectorAll(`#${elementId} tr`);
    rows.forEach(r => {
      const num = r.querySelector('.d2h-code-side-linenumber, .d2h-code-linenumber');
      if (num && num.textContent.trim() === String(line)) r.classList.add(cssClass);
    });
  }
};
```

- [ ] **Step 3: Typed API client**

```csharp
using System.Net.Http.Json;
namespace ReviewDojo.Client.Services;

public record DiffDto(int Id, int Ordinal, int SizeLines, string UnifiedDiffText, DateTime? StartedAt, DateTime? SubmittedAt);
public record FindingDto(string FilePath, int Line, string Category, string? Comment);
public record SubmitRequest(string Verdict, List<FindingDto> Findings);
public record ManifestBugDto(string FilePath, int LineStart, int LineEnd, string Category, string Severity, string Description);
public record ScoreDto(double Recall, double Precision, double SeverityWeightedRecall, double FalsePositiveRate, bool VerdictCorrect, long TimeMs, int Hits, int Misses, int FalsePositives);
public record MatchDto(string? BugFile, int? BugLine, string? FindingFile, int? FindingLine, double Credit);
public record RevealDto(int DiffId, List<ManifestBugDto> Manifest, ScoreDto Score, List<MatchDto> Matches);
public record CreateSessionRequest(string TargetRepoPath, string Tier, double? CleanRate, int? Seed);

public class ApiClient
{
    private readonly HttpClient _http;
    public ApiClient(HttpClient http) => _http = http;

    public async Task<int> CreateSession(CreateSessionRequest r) =>
        (await (await _http.PostAsJsonAsync("sessions", r)).Content.ReadFromJsonAsync<IdResp>())!.Id;
    public async Task<DiffDto> NextDiff(int sessionId) =>
        (await _http.PostAsync($"sessions/{sessionId}/diffs/next", null)).Content.ReadFromJsonAsync<DiffDto>().Result!;
    public Task StartDiff(int id) => _http.PostAsync($"diffs/{id}/start", null);
    public async Task<RevealDto> Submit(int id, SubmitRequest r) =>
        (await (await _http.PostAsJsonAsync($"diffs/{id}/submit", r)).Content.ReadFromJsonAsync<RevealDto>())!;
    public Task<StatsDto?> Stats() => _http.GetFromJsonAsync<StatsDto>("stats");
    private record IdResp(int Id);
}
public record StatsDto(int TotalScored, double MeanRecall, double MeanPrecision, double MeanFpRate, double MedianTimeMs, Dictionary<string,double> PerCategoryRecall, double CleanApprovalRate, double SeededRejectionRate, List<double> RecallTrend);
```

- [ ] **Step 4: Register HttpClient in `Program.cs`**

```csharp
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.Configuration["ApiBase"] ?? "https://localhost:7001/") });
builder.Services.AddScoped<ReviewDojo.Client.Services.ApiClient>();
```

- [ ] **Step 5: Build the client**

Run: `dotnet build src/ReviewDojo.Client`
Expected: succeeds.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(client): API client + diff2html interop"
```

---

## Task 15: Review page — diff viewer, findings, timer, submit

**Files:**
- Create: `src/ReviewDojo.Client/Pages/Home.razor`, `Pages/Review.razor`

- [ ] **Step 1: Home — start a session**

```razor
@page "/"
@inject ReviewDojo.Client.Services.ApiClient Api
@inject NavigationManager Nav

<h1>Review Dojo</h1>
<label>Target repo path <input @bind="repo" /></label>
<label>Difficulty
  <select @bind="tier"><option>Easy</option><option>Medium</option><option>Hard</option></select>
</label>
<button @onclick="Start">Start session (5 diffs)</button>

@code {
    string repo = ""; string tier = "Medium";
    async Task Start()
    {
        var id = await Api.CreateSession(new(repo, tier, 0.2, null));
        Nav.NavigateTo($"/review/{id}/0");
    }
}
```

- [ ] **Step 2: Review page — load diff, collect findings, timer, submit**

```razor
@page "/review/{SessionId:int}/{Ordinal:int}"
@inject ReviewDojo.Client.Services.ApiClient Api
@inject IJSRuntime JS
@inject NavigationManager Nav
@using ReviewDojo.Client.Services

@if (diff is null) { <p>Generating diff…</p> }
else
{
  <div class="toolbar">
    <span>Diff #@(Ordinal + 1) — @diff.SizeLines changed lines — @elapsed.ToString(@"mm\:ss")</span>
    <button @onclick="ToggleMode">@(mode == "side" ? "Unified" : "Side-by-side")</button>
  </div>
  <div id="diffview"></div>

  <h3>Findings</h3>
  <div class="add-finding">
    <input placeholder="file" @bind="fFile" />
    <input type="number" placeholder="line" @bind="fLine" />
    <select @bind="fCat">
      <option>Mechanical</option><option>EdgeCase</option><option>Contextual</option>
      <option>Abstraction</option><option>AgentTypical</option>
    </select>
    <input placeholder="comment (optional)" @bind="fComment" />
    <button @onclick="AddFinding">Add</button>
  </div>
  <ul>@foreach (var f in findings) { <li>@f.FilePath:@f.Line [@f.Category] @f.Comment</li> }</ul>

  <div class="verdict">
    <button @onclick='() => Submit("Approve")'>Approve</button>
    <button @onclick='() => Submit("RequestChanges")'>Request changes</button>
  </div>
}

@code {
    [Parameter] public int SessionId { get; set; }
    [Parameter] public int Ordinal { get; set; }
    DiffDto? diff; string mode = "line";
    string fFile = ""; int fLine; string fCat = "Mechanical"; string? fComment;
    readonly List<FindingDto> findings = new();
    System.Timers.Timer? timer; TimeSpan elapsed; DateTime startedAt;

    protected override async Task OnParametersSetAsync()
    {
        findings.Clear(); diff = null; StateHasChanged();
        diff = await Api.NextDiff(SessionId);
        await Api.StartDiff(diff.Id);
        startedAt = DateTime.UtcNow;
        timer = new System.Timers.Timer(1000);
        timer.Elapsed += (_, _) => { elapsed = DateTime.UtcNow - startedAt; InvokeAsync(StateHasChanged); };
        timer.Start();
    }

    protected override async Task OnAfterRenderAsync(bool first)
    {
        if (diff is not null)
            await JS.InvokeVoidAsync("dojoDiff.render", "diffview", diff.UnifiedDiffText, mode);
    }

    async Task ToggleMode() { mode = mode == "side" ? "line" : "side"; await JS.InvokeVoidAsync("dojoDiff.render", "diffview", diff!.UnifiedDiffText, mode); }
    void AddFinding() { findings.Add(new(fFile, fLine, fCat, fComment)); fFile=""; fLine=0; fComment=null; }

    async Task Submit(string verdict)
    {
        timer?.Stop();
        var reveal = await Api.Submit(diff!.Id, new(verdict, findings.ToList()));
        RevealCache.Last = reveal;   // static handoff to reveal page
        Nav.NavigateTo($"/reveal/{SessionId}/{Ordinal}");
    }
}
```

- [ ] **Step 3: Manual smoke (documented, run once API + client are up)**

```
Terminal A: cd src/ReviewDojo.Api && dotnet run
Terminal B: cd src/ReviewDojo.Client && dotnet run
Browser: open the client URL, enter a small real repo path, Medium, Start.
Expect: a diff renders; add a finding; Approve/Request changes navigates to reveal.
```

- [ ] **Step 4: Build**

Run: `dotnet build src/ReviewDojo.Client`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(client): review page with diff viewer, findings, timer, submit"
```

---

## Task 16: Reveal + Stats pages

**Files:**
- Create: `src/ReviewDojo.Client/Pages/Reveal.razor`, `Pages/Stats.razor`, `Services/RevealCache.cs`

- [ ] **Step 1: RevealCache handoff**

```csharp
namespace ReviewDojo.Client.Services;
public static class RevealCache { public static RevealDto? Last { get; set; } }
```

- [ ] **Step 2: Reveal page — overlay manifest, color-code hits/misses/FP, metrics, next**

```razor
@page "/reveal/{SessionId:int}/{Ordinal:int}"
@inject IJSRuntime JS
@inject NavigationManager Nav
@using ReviewDojo.Client.Services

@if (r is null) { <p>No reveal data.</p> }
else
{
  <h2>Result — diff #@(Ordinal + 1)</h2>
  <ul class="metrics">
    <li>Recall: @r.Score.Recall.ToString("P0")</li>
    <li>Precision: @r.Score.Precision.ToString("P0")</li>
    <li>Severity-weighted recall: @r.Score.SeverityWeightedRecall.ToString("P0")</li>
    <li>False-positive rate: @r.Score.FalsePositiveRate.ToString("P0")</li>
    <li>Verdict correct: @r.Score.VerdictCorrect</li>
    <li>Hits @r.Score.Hits · Misses @r.Score.Misses · FPs @r.Score.FalsePositives</li>
    <li>Time: @(r.Score.TimeMs/1000)s</li>
  </ul>
  <h3>Seeded bugs (@r.Manifest.Count)</h3>
  <ul>@foreach (var b in r.Manifest) {
    <li><b>@b.FilePath:@b.LineStart</b> [@b.Category / @b.Severity] — @b.Description</li> }</ul>
  @if (Ordinal + 1 < 5)
  { <button @onclick="Next">Next diff</button> }
  else
  { <button @onclick='() => Nav.NavigateTo("/stats")'>See cumulative stats</button> }
}

@code {
    [Parameter] public int SessionId { get; set; }
    [Parameter] public int Ordinal { get; set; }
    RevealDto? r;
    protected override void OnInitialized() => r = RevealCache.Last;
    void Next() => Nav.NavigateTo($"/review/{SessionId}/{Ordinal + 1}");
}
```

- [ ] **Step 3: Stats page**

```razor
@page "/stats"
@inject ReviewDojo.Client.Services.ApiClient Api
@using ReviewDojo.Client.Services

@if (s is null) { <p>Loading…</p> }
else
{
  <h2>Cumulative stats (@s.TotalScored diffs)</h2>
  <ul>
    <li>Mean recall: @s.MeanRecall.ToString("P0")</li>
    <li>Mean precision: @s.MeanPrecision.ToString("P0")</li>
    <li>Mean FP rate: @s.MeanFpRate.ToString("P0")</li>
    <li>Median time: @(s.MedianTimeMs/1000)s</li>
    <li>Calibration — clean approvals: @s.CleanApprovalRate.ToString("P0"), seeded rejections: @s.SeededRejectionRate.ToString("P0")</li>
  </ul>
  <h3>Recall by category</h3>
  <ul>@foreach (var kv in s.PerCategoryRecall) { <li>@kv.Key: @kv.Value.ToString("P0")</li> }</ul>
}

@code {
    StatsDto? s;
    protected override async Task OnInitializedAsync() => s = await Api.Stats();
}
```

- [ ] **Step 4: Build**

Run: `dotnet build src/ReviewDojo.Client`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(client): reveal overlay + cumulative stats pages"
```

---

## Task 17: CLI corpus miner + gen harness

**Files:**
- Create: `src/ReviewDojo.Cli/Program.cs`, `src/ReviewDojo.Cli/CorpusMiner.cs`
- Test: `tests/ReviewDojo.Tests/CorpusMinerTests.cs`

`dojo mine <repo>` scans git history via LibGit2Sharp for fix/revert commits (message matches `fix|revert|bug|patch`), captures before/after of changed hunks into `BugCorpusEntry` rows. `dojo gen <repo> --seed N` runs the generator once and prints the diff + manifest (dev harness).

- [ ] **Step 1: CorpusMiner with a testable classifier**

```csharp
using LibGit2Sharp;
using ReviewDojo.Core;
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
```

- [ ] **Step 2: Failing classifier test**

```csharp
using ReviewDojo.Cli;
using Xunit;

public class CorpusMinerTests
{
    [Theory]
    [InlineData("Fix null deref in parser", true)]
    [InlineData("Revert \"add caching\"", true)]
    [InlineData("Add new feature flag", false)]
    public void LooksLikeFix_ClassifiesByKeyword(string msg, bool expected)
        => Assert.Equal(expected, CorpusMiner.LooksLikeFix(msg));
}
```

- [ ] **Step 3: Run to verify fail then implement passes**

Run: `dotnet test --filter CorpusMinerTests`
Expected: FAIL then, after Step 1 exists, PASS.

- [ ] **Step 4: Program.cs command dispatch**

```csharp
using Microsoft.EntityFrameworkCore;
using ReviewDojo.Cli;
using ReviewDojo.Core;
using ReviewDojo.Data;
using ReviewDojo.Generator;

if (args.Length == 0) { Console.WriteLine("usage: dojo <mine|gen> <repoPath> [--seed N]"); return; }

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
                RepoPath = repo, Category = BugCategory.Mechanical,   // category unknown at mine time
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
        var http = new HttpClient();
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
```

- [ ] **Step 5: Wire few-shots into the generator (optional corpus use)**

In `DojoEndpoints` `diffs/next`, before calling the generator, load up to 3 recent corpus entries and pass as `BugFewShot`. (The generator already accepts `fewShots`.) Add an overload call:

```csharp
var shots = await db.Corpus.OrderByDescending(c => c.Id).Take(3)
    .Select(c => new BugFewShot(c.Category, c.BeforeSnippet, c.AfterSnippet, c.Message)).ToListAsync();
var g = await gen.GenerateAsync(s.TargetRepoPath, s.DifficultyTier, diffSeed, fewShots: shots);
```

- [ ] **Step 6: Run tests + build**

Run: `dotnet test --filter CorpusMinerTests && dotnet build src/ReviewDojo.Cli`
Expected: PASS + build succeeds.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(cli): corpus miner + gen harness, wire few-shots into generator"
```

---

## Task 18: README + full smoke

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write README**

Cover: prerequisites (.NET 8 SDK, `ANTHROPIC_API_KEY`), `dotnet ef database update` (or auto-migrate on boot), running API + client, generator config (`Anthropic:Model`, difficulty tiers, clean rate), `dojo mine`/`dojo gen` usage, and the security notes (key from env, `.env` gitignored, manifest server-only, generator never executes repo code).

- [ ] **Step 2: Full-solution test run**

Run: `dotnet test`
Expected: all suites green (Scoring, SizeSampler, Data, DiffBuilder, AnchorResolver, GeneratorDeterminism, ApiGating, ApiEndpoint, Stats, CorpusMiner).

- [ ] **Step 3: End-to-end manual smoke (Definition of Done)**

Point at a real repo, start a 5-diff session, review each, submit, see scores + revealed manifest, view cumulative stats. Confirm a clean diff (no manifest) appears indistinguishable pre-submit.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "docs: README with setup, generator config, security notes"
```

---

## Self-Review Notes (author)

- **Spec coverage:** data model (T6), two-step generator + anchor resolution (T7/T8/T10), clean-diff rate + determinism harness (T10), manifest-gating via DTO split (T11/T12), scoring incl. half-credit + severity + calibration (T3/T4/T13), diff viewer unified+side-by-side (T14/T15), reveal overlay (T16), per-category stats (T13/T16), corpus miner few-shots (T17), size distribution (T5), optional-but-stored justification (`Comment` in T6/T11). Security requirements restated in README (T18).
- **Deferred correctly:** no multi-user, no runtime verification, no adaptive difficulty, no spaced repetition, no LLM-graded comments.
- **Type consistency:** `ManifestBug`, `ReviewFinding`, `MatchResult`, `ScoreCard`, `BuiltDiff`, `RawManifestEntry`, `GeneratedDiff`, `DiffDto`/`RevealDto` used consistently across tasks.
- **Known implementer flags:** (1) confirm Anthropic `/v1/messages` schema + model id via `claude-api` skill in T9; (2) T12 submit-flow integration test needs a seeded test DB (documented inline).
