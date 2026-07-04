using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReviewDojo.Generator;

// Offline demo client. Drives the real generator pipeline without any network or API key by
// returning the same JSON shape the real model returns. It echoes the paths the generator sent
// and mutates real lines, so path-validation and anchor-resolution behave exactly as in real mode.
public class MockAnthropicClient : IAnthropicClient
{
    public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var content = request.Messages.Count > 0 ? request.Messages[0].Content : "";
        var files = ParseFiles(content);
        var json = request.System.Contains("INJECT") ? BuildInject(files) : BuildLegit(files);
        return Task.FromResult(json);
    }

    // Scan lines; a line starting with "### " begins a new file whose path is the rest of that
    // line. Subsequent lines (until the next "### " marker or end) form its body, joined by "\n".
    // Text before the first marker is preamble and is ignored.
    internal static List<(string Path, string Body)> ParseFiles(string content)
    {
        var result = new List<(string Path, string Body)>();
        string? path = null;
        var body = new List<string>();
        void Flush()
        {
            if (path != null) result.Add((path, string.Join("\n", body)));
        }
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("### "))
            {
                Flush();
                path = line.Substring(4).Trim();
                body = new List<string>();
            }
            else if (path != null)
            {
                body.Add(line);
            }
        }
        Flush();
        return result;
    }

    // Insert one benign comment line after the first line of each file so clean diffs are
    // non-empty and reviewable. Echoes the sent path unchanged; empty files are echoed as-is.
    internal static string BuildLegit(List<(string Path, string Body)> files)
    {
        var outFiles = files.Select(f =>
        {
            if (string.IsNullOrEmpty(f.Body))
                return new { path = f.Path, after = f.Body };

            var prefix = CommentPrefix(f.Body);
            var lines = f.Body.Split('\n').ToList();
            lines.Insert(1, $"{prefix} tidy: clarify intent");
            return new { path = f.Path, after = string.Join("\n", lines) };
        }).ToArray();

        return JsonSerializer.Serialize(new { files = outFiles });
    }

    static string CommentPrefix(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("#") || t.Contains("def ") || t.Contains("import "))
                return "#";
        }
        return "//";
    }

    // Inject one bug into the FIRST file only. Mutate the first matching pattern in the body;
    // the anchor is the trimmed text of the mutated line so AnchorResolver can locate it.
    internal static string BuildInject(List<(string Path, string Body)> files)
    {
        if (files.Count == 0)
            return JsonSerializer.Serialize(new { files = Array.Empty<object>(), bugs = Array.Empty<object>() });

        var (path, body) = files[0];

        if (!TryMutate(body, out var after, out var category, out var severity, out var description))
        {
            // No substantive content to mutate: return a clean (empty-bugs) result honestly.
            return JsonSerializer.Serialize(new
            {
                files = new[] { new { path, after = body } },
                bugs = Array.Empty<object>()
            });
        }

        var anchor = FindChangedLineAnchor(body, after);
        if (string.IsNullOrEmpty(anchor))
        {
            // Fall back to the final rule to guarantee a locatable, non-empty anchor.
            if (TryFinalFallback(body, out after, out category, out severity, out description))
                anchor = FindChangedLineAnchor(body, after);
        }

        if (string.IsNullOrEmpty(anchor))
            return JsonSerializer.Serialize(new
            {
                files = new[] { new { path, after = body } },
                bugs = Array.Empty<object>()
            });

        return JsonSerializer.Serialize(new
        {
            files = new[] { new { path, after } },
            bugs = new[] { new { path, anchor, category, severity, description } }
        });
    }

    static bool TryMutate(string body, out string after, out string category, out string severity, out string description)
    {
        (string Find, string Replace, string Cat, string Sev, string Desc)[] ops =
        {
            (" == ", " = ",  "Mechanical", "High",   "Comparison `==` changed to assignment `=`."),
            (" != ", " == ", "Mechanical", "High",   "Inequality flipped to equality."),
            (" <= ", " < ",  "Mechanical", "Medium", "Boundary changed `<=`→`<` (off-by-one)."),
            (" >= ", " > ",  "Mechanical", "Medium", "Boundary changed `>=`→`>` (off-by-one)."),
            (" && ", " || ", "Mechanical", "High",   "Logical AND changed to OR."),
        };

        foreach (var op in ops)
        {
            int idx = body.IndexOf(op.Find, StringComparison.Ordinal);
            if (idx >= 0)
            {
                after = body.Substring(0, idx) + op.Replace + body.Substring(idx + op.Find.Length);
                category = op.Cat; severity = op.Sev; description = op.Desc;
                return true;
            }
        }

        // Fallback: increment the first digit run by 1.
        var digit = Regex.Match(body, "\\d+");
        if (digit.Success)
        {
            var incremented = (long.Parse(digit.Value) + 1).ToString();
            after = body.Substring(0, digit.Index) + incremented + body.Substring(digit.Index + digit.Length);
            category = "Mechanical"; severity = "Medium"; description = "Numeric literal changed (off-by-one).";
            return true;
        }

        return TryFinalFallback(body, out after, out category, out severity, out description);
    }

    // Append "  // FIXME" to the first substantive line (trimmed length >= 3, not only braces/symbols).
    static bool TryFinalFallback(string body, out string after, out string category, out string severity, out string description)
    {
        after = body; category = "Mechanical"; severity = "Low"; description = "Suspicious trailing edit.";
        var lines = body.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length >= 3 && t.Any(c => char.IsLetterOrDigit(c)))
            {
                lines[i] = lines[i] + "  // FIXME";
                after = string.Join("\n", lines);
                return true;
            }
        }
        return false;
    }

    // Split both bodies into lines, find the first differing index, return that line trimmed.
    static string FindChangedLineAnchor(string before, string after)
    {
        var b = before.Split('\n');
        var a = after.Split('\n');
        int n = Math.Min(b.Length, a.Length);
        for (int i = 0; i < n; i++)
            if (b[i] != a[i])
                return a[i].Trim();
        if (a.Length > b.Length)
            return a[b.Length].Trim();
        return "";
    }
}
