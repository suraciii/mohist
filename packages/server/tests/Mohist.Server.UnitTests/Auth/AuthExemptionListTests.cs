using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Identity;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class AuthExemptionListTests
{
    [Theory]
    [InlineData("GET", "/api/health", true)]
    [InlineData("GET", "/api/health/", true)]
    [InlineData("POST", "/api/health", false)]
    [InlineData("POST", "/api/auth/session", true)]
    [InlineData("POST", "/api/auth/device/code", true)]
    [InlineData("POST", "/api/auth/token", true)]
    [InlineData("POST", "/api/auth/device", false)]
    [InlineData("GET", "/api/auth/session", false)]
    [InlineData("POST", "/api/github-connections/github_abc123/ingress", true)]
    [InlineData("POST", "/api/github-connections/github_abc123/ingress/", true)]
    [InlineData("POST", "/api/github-connections/ingress", false)]
    [InlineData("POST", "/api/github-connections/github_abc123/ingress/extra", false)]
    [InlineData("GET", "/api/github-connections/github_abc123/ingress", false)]
    [InlineData("GET", "/api/projects", false)]
    [InlineData("POST", "/api/projects", false)]
    [InlineData("GET", "/hubs/events/negotiate", false)]
    [InlineData("GET", "/hubs/runner/negotiate", false)]
    [InlineData("GET", "/otel/api/status", false)]
    public void IsExempt_MatchesTheClosedList(string method, string path, bool expected)
    {
        Assert.Equal(expected, AuthExemptionList.IsExempt(new PathString(path), method));
    }
}
