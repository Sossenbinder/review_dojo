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
        foreach (var l in lines)
            if (l.Text.Contains(needle)) return l.Line;
        return -1;
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
            var model = InlineDiffBuilder.Instance.BuildDiffModel(ch.Before, ch.After);
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
