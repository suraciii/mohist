using Mohist.Server.Infrastructure.Workspace;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Workspace;

[Trait("level", "L0")]
public sealed class MohistWorkspaceLayoutTests
{
    [Theory]
    [InlineData("my-project", "my-project")]
    [InlineData("My Project!", "my-project")]
    [InlineData("  spaced  out  ", "spaced-out")]
    [InlineData("foo_bar.baz", "foo-bar-baz")]
    [InlineData("Café", "caf")]
    [InlineData("测试-project", "project")]
    [InlineData("", "project")]
    [InlineData(null, "project")]
    public void Slug_MatchesRunnerAlgorithm(string? input, string expected)
    {
        Assert.Equal(expected, MohistWorkspaceLayout.Slug(input ?? string.Empty));
    }
}
