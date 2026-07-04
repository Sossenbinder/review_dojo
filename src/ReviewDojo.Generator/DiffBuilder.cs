using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using System.Text;

namespace ReviewDojo.Generator;

public record FileChange(string Path, string Before, string After);

public class BuiltDiff
{
    public string UnifiedText { get; init; } = "";
    public int ChangedLineCount { get; init; }
    // path -> list of (newLineNumber, text, added) for lines present in the after-file.
    // Added==true for Inserted lines, false for Unchanged context lines.
    public Dictionary<string, List<(int Line, string Text, bool Added)>> NewLines { get; init; } = new();

    // First new-file line number whose text contains the anchor snippet (trimmed).
    // Prefers ADDED lines over context so an injected bug's anchor resolves to the
    // added line, not an identical-looking context line appearing earlier.
    public int NewLineOf(string path, string anchor)
    {
        var needle = anchor.Trim();
        if (!NewLines.TryGetValue(path, out var lines)) return -1;
        foreach (var l in lines)
            if (l.Added && l.Text.Contains(needle)) return l.Line;
        foreach (var l in lines)
            if (!l.Added && l.Text.Contains(needle)) return l.Line;
        return -1;
    }
}

public class DiffBuilder
{
    public BuiltDiff Build(IEnumerable<FileChange> changes)
    {
        var sb = new StringBuilder();
        int changed = 0;
        var newLines = new Dictionary<string, List<(int, string, bool)>>();

        foreach (var ch in changes)
        {
            var model = InlineDiffBuilder.Instance.BuildDiffModel(ch.Before, ch.After);

            // Precompute hunk-header counts: before = Unchanged+Deleted, after = Unchanged+Inserted.
            int beforeCount = model.Lines.Count(l => l.Type is ChangeType.Unchanged or ChangeType.Deleted);
            int afterCount = model.Lines.Count(l => l.Type is ChangeType.Unchanged or ChangeType.Inserted);

            sb.AppendLine($"--- a/{ch.Path}");
            sb.AppendLine($"+++ b/{ch.Path}");
            // Single hunk per file (we diff whole files); numbering starts at line 1 both sides
            // so diff2html's gutter matches our new-file line numbering.
            sb.AppendLine($"@@ -1,{beforeCount} +1,{afterCount} @@");
            int newLineNo = 0;
            var perFile = new List<(int, string, bool)>();
            foreach (var line in model.Lines)
            {
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        newLineNo++; changed++;
                        sb.AppendLine("+" + line.Text);
                        perFile.Add((newLineNo, line.Text, true));
                        break;
                    case ChangeType.Deleted:
                        changed++;
                        sb.AppendLine("-" + line.Text);
                        break;
                    case ChangeType.Unchanged:
                        newLineNo++;
                        sb.AppendLine(" " + line.Text);
                        perFile.Add((newLineNo, line.Text, false));
                        break;
                    default:
                        newLineNo++;
                        sb.AppendLine(" " + line.Text);
                        perFile.Add((newLineNo, line.Text, false));
                        break;
                }
            }
            newLines[ch.Path] = perFile;
        }
        return new BuiltDiff { UnifiedText = sb.ToString(), ChangedLineCount = changed, NewLines = newLines };
    }
}
