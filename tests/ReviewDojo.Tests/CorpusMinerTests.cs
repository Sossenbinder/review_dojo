using ReviewDojo.Cli;
using Xunit;

public class CorpusMinerTests
{
    [Theory]
    [InlineData("Fix null deref in parser", true)]
    [InlineData("Revert \"add caching\"", true)]
    [InlineData("Add new feature flag", false)]
    public void LooksLikeFix_ClassifiesByKeyword(string msg, bool expected)
        => Assert.Equal(expected, CorpusMiner.LooksLikeFix(msg));
}
