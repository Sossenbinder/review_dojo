namespace ReviewDojo.Client.Services;

public static class RevealCache
{
    public static RevealDto? Last { get; set; }
    // The findings the user submitted for the last diff — used on the reveal page to
    // annotate each finding's outcome (category picked, comment) alongside the match result.
    public static List<FindingDto>? Findings { get; set; }
}
