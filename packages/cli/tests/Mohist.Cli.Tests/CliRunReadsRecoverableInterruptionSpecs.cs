using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public partial class CliRunReadsSpecs
{
    [Fact]
    public async Task View_RecoverableInterruptionRendersReasonAndDeadlineWithoutFailure()
    {
        var interruption = new
        {
            reasonCode = "runner-lost",
            workId = "build.1",
            ownerId = WrId,
            recordedAt = "2026-08-15T01:00:00Z",
            recoveryDeadlineAt = "2026-08-15T01:15:00Z",
        };
        var (handler, http, output, error, fs, executor) = CliTestFactory.CreateSync(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri?.PathAndQuery == $"/api/workflow-runs/{WrId}")
                return RecordingHttpHandler.Json(new { success = true, data = SampleRunDetail(WrId, "recoverable-interrupted", "build", interruption: interruption) });
            return null!;
        });

        var exitCode = await MohistCliCommands.RunAsync(http, ["run", "view", WrId], output, error, fs, executor);

        Assert.Equal(0, exitCode);
        var stdout = output.ToString();
        Assert.Contains("status:        recoverable-interrupted", stdout);
        Assert.Contains("recoverable interruption:", stdout);
        Assert.Contains("reason:    runner-lost", stdout);
        Assert.Contains("deadline:  2026-08-15T01:15:00Z", stdout);
        Assert.DoesNotContain("failure:", stdout, StringComparison.OrdinalIgnoreCase);
    }
}
