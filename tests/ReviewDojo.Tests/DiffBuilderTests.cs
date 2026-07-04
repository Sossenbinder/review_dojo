using ReviewDojo.Generator;
using Xunit;

public class DiffBuilderTests
{
    [Fact]
    public void ProducesUnifiedDiffWithHunkHeader()
    {
        var b = new DiffBuilder();
        var d = b.Build(new[] { new FileChange("a.cs", "line1\nline2\n", "line1\nCHANGED\n") });
        Assert.Contains("--- a/a.cs", d.UnifiedText);
        Assert.Contains("+++ b/a.cs", d.UnifiedText);
        Assert.Contains("+CHANGED", d.UnifiedText);
        Assert.Contains("-line2", d.UnifiedText);
    }

    [Fact]
    public void LineMap_FindsNewLineNumberOfAddedContent()
    {
        var b = new DiffBuilder();
        var d = b.Build(new[] { new FileChange("a.cs", "x\ny\nz\n", "x\ny\nNEW\nz\n") });
        int line = d.NewLineOf("a.cs", "NEW");
        Assert.Equal(3, line);  // NEW is the 3rd line of the after-file
    }

    [Fact]
    public void ChangedLineCount_CountsAddsAndRemoves()
    {
        var b = new DiffBuilder();
        var d = b.Build(new[] { new FileChange("a.cs", "a\nb\n", "a\nB\nc\n") });
        Assert.True(d.ChangedLineCount >= 2);
    }
}
