using ReviewDojo.Core;
using ReviewDojo.Core.Scoring;
using Xunit;

public class SizeSamplerTests
{
    [Fact]
    public void SameSeed_IsDeterministic()
    {
        var a = new SizeSampler(seed: 42);
        var b = new SizeSampler(seed: 42);
        Assert.Equal(a.Sample(DifficultyTier.Medium), b.Sample(DifficultyTier.Medium));
    }

    [Fact]
    public void Samples_StayWithinClamp()
    {
        var s = new SizeSampler(seed: 7);
        for (int i = 0; i < 500; i++)
        {
            int n = s.Sample(DifficultyTier.Hard);
            Assert.InRange(n, 10, 800);
        }
    }

    [Fact]
    public void MedianTier_CentersNear150()
    {
        var s = new SizeSampler(seed: 1);
        var vals = Enumerable.Range(0, 2000).Select(_ => s.Sample(DifficultyTier.Medium)).OrderBy(x => x).ToList();
        int median = vals[vals.Count / 2];
        Assert.InRange(median, 110, 200);
    }
}
