using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Identity;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class IntegrationProjectConstraintTests
{
    [Fact]
    public void ExtractProjectRef_PrefersTheRouteValue_OverTheQueryParameter()
    {
        var request = NewRequest();
        request.RouteValues["projectRef"] = "proj_a";
        request.QueryString = new QueryString("?projectRef=proj_b");

        Assert.Equal("proj_a", IntegrationProjectConstraint.ExtractProjectRef(request));
    }

    [Fact]
    public void ExtractProjectRef_FallsBackToTheQueryParameter()
    {
        var request = NewRequest();
        request.QueryString = new QueryString("?projectRef=proj_a");

        Assert.Equal("proj_a", IntegrationProjectConstraint.ExtractProjectRef(request));
    }

    [Fact]
    public void ExtractProjectRef_ReturnsNull_WhenTheRequestHasNoProject()
    {
        Assert.Null(IntegrationProjectConstraint.ExtractProjectRef(NewRequest()));
    }

    [Theory]
    [InlineData("proj_a", "proj_a", true)]
    [InlineData("proj_a", "proj_b", false)]
    [InlineData("proj_a", null, false)]
    [InlineData(null, "proj_a", false)]
    public void IsSatisfied_ComparesTheConstrainedAndRequestProjectIds(
        string? constrainedProjectId,
        string? requestProjectId,
        bool expected)
    {
        Assert.Equal(
            expected,
            IntegrationProjectConstraint.IsSatisfied(constrainedProjectId, requestProjectId));
    }

    private static HttpRequest NewRequest() => new DefaultHttpContext().Request;
}
