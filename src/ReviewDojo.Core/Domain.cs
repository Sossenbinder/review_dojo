namespace ReviewDojo.Core;

// A hidden ground-truth bug. Server-only; never leaves the API before submit.
public record ManifestBug(
    string FilePath, int LineStart, int LineEnd,
    BugCategory Category, Severity Severity, string Description);

// A reviewer's claim.
public record ReviewFinding(
    string FilePath, int Line, BugCategory Category, string? Comment);

// Credit: 1.0 exact, 0.5 category mismatch within proximity, 0.0 unmatched.
public record MatchResult(
    ManifestBug? Bug, ReviewFinding? Finding, double Credit, bool CategoryMatched);
