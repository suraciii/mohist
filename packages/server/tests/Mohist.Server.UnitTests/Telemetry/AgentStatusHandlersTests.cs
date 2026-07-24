using Microsoft.Extensions.Primitives;
using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class AgentStatusHandlersTests
{
    [Fact]
    public void Blank_query_falls_back_to_first_nonblank_header()
    {
        var selected = AgentStatusHandlers.SelectProjectRef(
            new StringValues([" ", ""]),
            new StringValues([" ", " project-name ", "ignored"]));

        Assert.Equal("project-name", selected);
    }

    [Fact]
    public void Blank_selectors_return_null()
    {
        var selected = AgentStatusHandlers.SelectProjectRef(
            new StringValues([" ", ""]),
            new StringValues(["", "  "]));

        Assert.Null(selected);
    }

    [Fact]
    public void First_nonblank_query_wins_conflicting_header()
    {
        var selected = AgentStatusHandlers.SelectProjectRef(
            new StringValues([" ", " project-query ", "ignored"]),
            new StringValues(["project-header"]));

        Assert.Equal("project-query", selected);
    }
}
