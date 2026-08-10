using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateOutcomeSpecs
{
    [Fact]
    public async Task UpdateAll_WhenServerReachable_PostsOutcomeToServer()
    {
        var tempRoot = "/mohist-tests/mohist-outcome-posted";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedManagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+abc123");
        f.Commands.SetStdoutFor("git", _ => true, "abc123");
        var systemInfo = UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "abc123", runnerStatus: "active");
        var handler = new OutcomeCapturingHttpHandler(systemInfo);
        var updater = f.BuildUpdater(handler, unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        Assert.NotNull(handler.LastOutcomeRequest);
        var outcome = handler.LastOutcomeRequest!;
        Assert.False(string.IsNullOrWhiteSpace(outcome.JobId));
        Assert.Equal("succeeded", outcome.Status);
        Assert.Equal("succeeded", outcome.Outcome);
        Assert.Null(outcome.UnavailableCapability);
        Assert.NotNull(outcome.Logs);
        Assert.NotEmpty(outcome.Logs!);
        Assert.DoesNotContain(outcome.Logs!, l => l.Stage == "Updating CLI");
        Assert.Contains(outcome.Logs!, l => l.Stage == "Preparing workflow runner");
        Assert.Contains(outcome.Logs!, l => l.Stage == "Verifying workflow runtime");
        Assert.Equal("abc123", outcome.SourceHead);
        Assert.Contains("Update outcome persisted to server.", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateAll_WhenServerUnreachable_SkipsOutcomePostWithMessage()
    {
        var tempRoot = "/mohist-tests/mohist-outcome-unreachable";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedManagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+abc123");
        f.Commands.SetStdoutFor("git", _ => true, "abc123");
        var systemInfo = UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "abc123", runnerStatus: "active");
        var handler = new OutcomeCapturingHttpHandler(systemInfo)
        {
            OutcomeResponseStatusCode = HttpStatusCode.ServiceUnavailable,
        };
        var updater = f.BuildUpdater(handler, unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        var output = f.Stdout.ToString();
        Assert.Contains("Update complete. Mohist is ready.", output);
        Assert.Contains("Could not persist update outcome to server", output);
        Assert.DoesNotContain("Update outcome persisted to server.", output);
    }

    [Fact]
    public async Task UpdateAll_WhenInterruptedBeforeRunnerStop_DoesNotPostOutcomeToServer()
    {
        var tempRoot = "/mohist-tests/mohist-cancel-no-post";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();

        var handler = new OutcomeCapturingHttpHandler(UpdateTestFactory.HealthySystemInfoJson());
        var updater = f.BuildUpdater(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", cts.Token, continueAfterCliUpdate: true);

        Assert.Equal(130, exitCode);
        Assert.Contains("No recovery needed", f.Stdout.ToString());
        Assert.Contains("no outcome was posted", f.Stdout.ToString());
        Assert.Null(handler.LastOutcomeRequest);
    }

    [Fact]
    public async Task UpdateAll_WebUiCanReadCliOutcomeViaStatusEndpoint()
    {
        var tempRoot = "/mohist-tests/mohist-outcome-webui";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedManagedSkillAssets();
        f.SeedRunnerUnit();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+abc123");
        f.Commands.SetStdoutFor("git", _ => true, "abc123");
        var systemInfo = UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "abc123", runnerStatus: "active");
        var handler = new OutcomeCapturingHttpHandler(systemInfo);
        var updater = f.BuildUpdater(handler, unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        Assert.NotNull(handler.LastOutcomeRequest);
        var postedJobId = handler.LastOutcomeRequest!.JobId;
        Assert.False(string.IsNullOrWhiteSpace(postedJobId));

        // The "Web UI" call to GET /api/system/update/status would invoke the
        // server-side SystemUpdateService. The captured handler records the
        // same JSON body the server would respond with for the GET, simulating
        // the round-trip.
        var statusJson = handler.BuildStatusResponseJson();
        using var statusDoc = System.Text.Json.JsonDocument.Parse(statusJson);
        var root = statusDoc.RootElement;
        Assert.Equal(postedJobId, root.GetProperty("jobId").GetString());
        Assert.Equal("succeeded", root.GetProperty("status").GetString());
        Assert.Equal("succeeded", root.GetProperty("outcome").GetString());
        Assert.Equal("abc123", root.GetProperty("sourceHead").GetString());
    }
}
