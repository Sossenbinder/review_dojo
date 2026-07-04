using System.Reflection;
using ReviewDojo.Api;
using Xunit;

public class ApiGatingTests
{
    [Fact]
    public void DiffDto_HasNoGroundTruthMembers()
    {
        var props = typeof(DiffDto).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(props, p =>
            p.Name.Contains("Manifest", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Bug", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Severity", StringComparison.OrdinalIgnoreCase));
    }
}
