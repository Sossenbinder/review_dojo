using System.Text.Json;
using ReviewDojo.Core;

namespace ReviewDojo.Generator;

public record GeneratedDiff(string UnifiedText, bool IsClean, int ChangedLineCount, List<ManifestBug> Manifest);

// Thrown when the model's response cannot be parsed into the expected shape.
// Carries only a short, non-sensitive prefix of the offending text.
public class GeneratorException : Exception
{
    public GeneratorException(string message, Exception? inner = null) : base(message, inner) { }
}

public class DiffGenerator
{
    private readonly IAnthropicClient _client;
    private readonly string _model;
    private readonly DiffBuilder _diffBuilder = new();
    private readonly AnchorResolver _resolver = new();

    public DiffGenerator(IAnthropicClient client, string model) { _client = client; _model = model; }

    public async Task<GeneratedDiff> GenerateAsync(
        string repoPath, DifficultyTier tier, int seed, int? forceMistakeCount = null,
        double cleanRate = 0.2, IReadOnlyList<BugFewShot>? fewShots = null, CancellationToken ct = default)
    {
        var files = LocusSelector.Select(repoPath, tier, seed);
        var before = files.ToDictionary(f => f.RelPath, f => f.Text);

        var step1Json = await _client.CompleteAsync(new LlmRequest(
            _model, PromptLibrary.LegitChangeSystem,
            new[] { new LlmMessage("user", PromptLibrary.LegitChangeUser(before, tier)) }), ct);
        var afterClean = ParseFiles(step1Json);
        var cleanChanges = afterClean.Select(kv =>
        {
            var beforeKey = MatchBeforeKey(before, kv.Key);
            return new FileChange(beforeKey, before[beforeKey], kv.Value);
        }).ToList();

        int m = forceMistakeCount ?? MistakeCountPolicy.Decide(tier, seed, cleanRate);
        if (m == 0)
        {
            var cleanDiff = _diffBuilder.Build(cleanChanges);
            return new GeneratedDiff(cleanDiff.UnifiedText, true, cleanDiff.ChangedLineCount, new());
        }

        var step2Json = await _client.CompleteAsync(new LlmRequest(
            _model, PromptLibrary.InjectSystem,
            new[] { new LlmMessage("user", PromptLibrary.InjectUser(afterClean, m, tier, fewShots)) }), ct);
        var (afterBuggy, rawBugs) = ParseFilesAndBugs(step2Json);

        var buggyChanges = afterBuggy.Select(kv =>
        {
            var beforeKey = MatchBeforeKey(before, kv.Key);
            return new FileChange(beforeKey, before[beforeKey], kv.Value);
        }).ToList();
        var builtDiff = _diffBuilder.Build(buggyChanges);
        var manifest = _resolver.ResolveAll(builtDiff, rawBugs);

        return new GeneratedDiff(builtDiff.UnifiedText, manifest.Count == 0, builtDiff.ChangedLineCount, manifest);
    }

    // Resolves a model-returned path to a known before-file key. Tries exact match,
    // then a normalized (case-insensitive, separator-agnostic) match. Throws if unknown
    // rather than silently diffing against an empty before-file.
    static string MatchBeforeKey(Dictionary<string, string> before, string returnedPath)
    {
        if (before.ContainsKey(returnedPath)) return returnedPath;
        string Norm(string p) => p.Replace('\\', '/').Trim().TrimStart('.', '/');
        var target = Norm(returnedPath);
        foreach (var key in before.Keys)
            if (string.Equals(Norm(key), target, StringComparison.OrdinalIgnoreCase))
                return key;
        throw new InvalidOperationException(
            $"Model returned unknown file path '{returnedPath}'. Known before-file keys: [{string.Join(", ", before.Keys)}].");
    }

    // Strips a leading/trailing markdown code fence and returns the substring between the
    // first '{' and the last '}', so fenced JSON responses parse cleanly.
    static string StripFences(string text)
    {
        var t = text.Trim();
        int start = t.IndexOf('{');
        int end = t.LastIndexOf('}');
        if (start >= 0 && end >= start) return t.Substring(start, end - start + 1);
        return t;
    }

    static string Prefix(string text)
        => text.Length <= 120 ? text : text.Substring(0, 120);

    static Dictionary<string, string> ParseFiles(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripFences(json));
            return doc.RootElement.GetProperty("files").EnumerateArray()
                .ToDictionary(e => e.GetProperty("path").GetString()!, e => e.GetProperty("after").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GeneratorException($"Failed to parse model files JSON. Prefix: {Prefix(json)}", ex);
        }
    }

    static (Dictionary<string,string>, List<RawManifestEntry>) ParseFilesAndBugs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripFences(json));
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
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new GeneratorException($"Failed to parse model inject JSON. Prefix: {Prefix(json)}", ex);
        }
    }
}
