using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

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
}
