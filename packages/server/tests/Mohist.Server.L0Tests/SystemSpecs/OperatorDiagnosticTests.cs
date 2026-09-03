using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.L0Tests.SystemSpecs;

[Trait("level", "L0")]
public sealed class OperatorDiagnosticTests
{
    [Fact]
    public void Summarize_RemovesStackFramesControlsAndPaths()
    {
        var error = new InvalidOperationException(
            "handler failed at /tmp/private/Handler.cs\u001b[31m\n   at Example.Handler() in /tmp/private/Handler.cs:line 42");

        var summary = OperatorDiagnostic.Summarize(error);

        Assert.Equal("InvalidOperationException: handler failed at [path]", summary);
        Assert.DoesNotContain("\u001b", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/private", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("failure at Namespace.Handler() in /tmp/private/Handler.cs:line 42", "Namespace.Handler", "/tmp/private")]
    [InlineData("failed path=/srv/private/db.sqlite", "/srv/private", "db.sqlite")]
    [InlineData("failed file:///srv/private/db.sqlite", "/srv/private", "db.sqlite")]
    [InlineData(@"failed path=C:\\private\\db.sqlite", @"C:\\private", "db.sqlite")]
    [InlineData(@"failed path=\\fileserver\share\secret.txt", "fileserver", "secret.txt")]
    public void Summarize_RedactsSingleLineFramesAndEmbeddedPaths(
        string value,
        string firstSecret,
        string secondSecret)
    {
        var summary = OperatorDiagnostic.Summarize(value);

        Assert.NotNull(summary);
        Assert.DoesNotContain(firstSecret, summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondSecret, summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[", summary, StringComparison.Ordinal);
    }
}
