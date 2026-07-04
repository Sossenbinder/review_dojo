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

        // Anchors are single-line verbatim substrings, so LineEnd == LineStart.
        return new ManifestBug(raw.FilePath, line, line, raw.Category, raw.Severity, raw.Description);
    }

    public List<ManifestBug> ResolveAll(BuiltDiff diff, IEnumerable<RawManifestEntry> raws)
        => raws.Select(r => Resolve(diff, r)).Where(b => b != null).Select(b => b!).ToList();
}
