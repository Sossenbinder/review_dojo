using ReviewDojo.Core;
using ReviewDojo.Generator;
using Xunit;

public class AnchorResolverTests
{
    static BuiltDiff Diff() => new DiffBuilder().Build(new[] {
        new FileChange("a.cs", "int x=0;\n", "int x=0;\nif (n = 5) {}\n") });

    [Fact]
    public void ResolvesAnchorToNewLineRange()
    {
        var r = new AnchorResolver();
        var raw = new RawManifestEntry("a.cs", "if (n = 5)", BugCategory.Mechanical, Severity.High, "assignment not comparison");
        var bug = r.Resolve(Diff(), raw);
        Assert.NotNull(bug);
        Assert.Equal(2, bug!.LineStart);
        Assert.Equal(2, bug.LineEnd);
        Assert.Equal(BugCategory.Mechanical, bug.Category);
    }

    [Fact]
    public void UnlocatableAnchor_IsDropped()
    {
        var r = new AnchorResolver();
        var raw = new RawManifestEntry("a.cs", "this text is not in the diff", BugCategory.Mechanical, Severity.Low, "d");
        Assert.Null(r.Resolve(Diff(), raw));
    }
}
