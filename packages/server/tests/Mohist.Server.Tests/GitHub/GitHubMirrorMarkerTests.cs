using Mohist.Server.GitHub.Domain;
using Xunit;

namespace Mohist.Server.Tests.GitHub;

[Trait("level", "L0")]
public sealed class GitHubMirrorMarkerTests
{
    [Fact]
    public void AppendAndStrip_PreserveBodyTrailingWhitespace()
    {
        const string marker = "<!-- mohist:mirror:link-1 -->";
        const string body = "body with trailing spaces  \t\n";

        var mirrored = GitHubMirrorMarker.Append(body, marker);

        Assert.Equal(body, GitHubMirrorMarker.Strip(mirrored, marker));
    }

    [Fact]
    public void Strip_LeavesUnmarkedTrailingWhitespaceUntouched()
    {
        const string marker = "<!-- mohist:mirror:link-1 -->";
        const string body = "body  \t\n";

        Assert.Equal(body, GitHubMirrorMarker.Strip(body, marker));
    }

    [Fact]
    public void EmptyBody_RoundTripsAsEmptyString()
    {
        const string marker = "<!-- mohist:mirror:link-1 -->";

        Assert.Equal(string.Empty, GitHubMirrorMarker.Strip(
            GitHubMirrorMarker.Append(string.Empty, marker), marker));
    }
}
