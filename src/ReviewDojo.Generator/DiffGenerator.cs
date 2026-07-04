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
        var files = LocusSelector.Select(repoPath, tier, seed);
        var before = files.ToDictionary(f => f.RelPath, f => f.Text);

        var step1Json = await _client.CompleteAsync(new LlmRequest(
            _model, PromptLibrary.LegitChangeSystem,
            new[] { new LlmMessage("user", PromptLibrary.LegitChangeUser(before, tier)) }), ct);
        var afterClean = ParseFiles(step1Json);
        var cleanChanges = afterClean.Select(kv => new FileChange(kv.Key, before.GetValueOrDefault(kv.Key, ""), kv.Value)).ToList();

        int m = forceMistakeCount ?? MistakeCountPolicy.Decide(tier, seed, cleanRate: 0.2);
        if (m == 0)
        {
            var cleanDiff = _diffBuilder.Build(cleanChanges);
            return new GeneratedDiff(cleanDiff.UnifiedText, true, cleanDiff.ChangedLineCount, new());
        }

        var step2Json = await _client.CompleteAsync(new LlmRequest(
            _model, PromptLibrary.InjectSystem,
            new[] { new LlmMessage("user", PromptLibrary.InjectUser(afterClean, m, tier, fewShots)) }), ct);
        var (afterBuggy, rawBugs) = ParseFilesAndBugs(step2Json);

        var buggyChanges = afterBuggy.Select(kv => new FileChange(kv.Key, before.GetValueOrDefault(kv.Key, ""), kv.Value)).ToList();
        var builtDiff = _diffBuilder.Build(buggyChanges);
        var manifest = _resolver.ResolveAll(builtDiff, rawBugs);

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
