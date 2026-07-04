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

public record CreateSessionRequest(string TargetRepoPath, DifficultyTier Tier, double? CleanRate, int? Seed);
