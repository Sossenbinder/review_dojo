using ReviewDojo.Core;
using ReviewDojo.Generator;
using Xunit;

public class LocusSelectorTests
{
    [Fact]
    public void ExcludesRootLevelBinObjGit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dojo-locus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "bin", "Gen.cs"), "class Gen {}");
        File.WriteAllText(Path.Combine(dir, "obj", "Obj.cs"), "class Obj {}");
        File.WriteAllText(Path.Combine(dir, "src", "Real.cs"), "class Real {}");
        try
        {
            for (int seed = 0; seed < 20; seed++)
            {
                var picked = LocusSelector.Select(dir, DifficultyTier.Hard, seed);
                Assert.All(picked, f => Assert.DoesNotContain("Gen.cs", f.RelPath));
                Assert.All(picked, f => Assert.DoesNotContain("Obj.cs", f.RelPath));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ThrowsWhenOnlyBuildArtifactsExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dojo-locus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.WriteAllText(Path.Combine(dir, "bin", "Only.cs"), "class Only {}");
        try { Assert.Throws<InvalidOperationException>(() => LocusSelector.Select(dir, DifficultyTier.Easy, 0)); }
        finally { Directory.Delete(dir, true); }
    }
}
