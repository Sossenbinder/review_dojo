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
