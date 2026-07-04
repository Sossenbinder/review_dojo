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
public record StatsDto(int TotalScored, double MeanRecall, double MeanPrecision, double MeanFpRate, double MedianTimeMs, Dictionary<string,double> PerCategoryRecall, double CleanApprovalRate, double SeededRejectionRate, List<double> RecallTrend);

public class ApiClient
{
    private readonly HttpClient _http;
    public ApiClient(HttpClient http) => _http = http;

    private record IdResp(int Id);
    private record ErrResp(string? Error);

    // Surface a server-provided { error } message (e.g. LLM failure) instead of a raw 500.
    private static async Task Ensure(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        string? msg = null;
        try { msg = (await resp.Content.ReadFromJsonAsync<ErrResp>())?.Error; } catch { /* not a json error body */ }
        throw new InvalidOperationException(msg ?? $"Request failed ({(int)resp.StatusCode} {resp.ReasonPhrase}).");
    }

    public async Task<int> CreateSession(CreateSessionRequest r)
    {
        var resp = await _http.PostAsJsonAsync("api/sessions", r);
        await Ensure(resp);
        var id = await resp.Content.ReadFromJsonAsync<IdResp>();
        return id!.Id;
    }

    public async Task<DiffDto> NextDiff(int sessionId)
    {
        var resp = await _http.PostAsync($"api/sessions/{sessionId}/diffs/next", null);
        await Ensure(resp);
        return (await resp.Content.ReadFromJsonAsync<DiffDto>())!;
    }

    public async Task StartDiff(int id)
    {
        var resp = await _http.PostAsync($"api/diffs/{id}/start", null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<RevealDto> Submit(int id, SubmitRequest r)
    {
        var resp = await _http.PostAsJsonAsync($"api/diffs/{id}/submit", r);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RevealDto>())!;
    }

    public Task<StatsDto?> Stats() => _http.GetFromJsonAsync<StatsDto>("api/stats");
}
