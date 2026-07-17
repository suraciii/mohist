using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateServerSpecs
{
    [Fact]
    public async Task UpdateServer_BuildsCurrentSourceAndRestarts()
    {
        var f = new UpdateTestFactory();
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, f.Commands.ExecutedCommands.Count);
        Assert.Equal("dotnet", f.Commands.ExecutedCommands[0].FileName);
        Assert.Equal(new[] { "build", "Mohist.sln" }, f.Commands.ExecutedCommands[0].Args);
        Assert.Equal("/repo", f.Commands.ExecutedCommands[0].WorkingDirectory);
        Assert.Equal("systemctl", f.Commands.ExecutedCommands[1].FileName);
        Assert.Equal(new[] { "--user", "restart", "mohist.service" }, f.Commands.ExecutedCommands[1].Args);
    }

    [Fact]
    public async Task UpdateServer_WaitsForReadinessAfterRestart()
    {
        var f = new UpdateTestFactory();
        var readiness = new SequenceHttpHandler(
            null,
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var updater = f.BuildUpdater(readiness, serverReadyTimeout: TimeSpan.FromSeconds(30));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, readiness.Requests);
        Assert.Equal(["/api/health", "/api/health", "/", "/assets/app.js"], readiness.Paths);
        Assert.Contains("Server is ready.", f.Stdout.ToString());
    }

    [Fact]
    public async Task UpdateServer_AfterSuccess_AnnouncesRunnerNotRefreshed()
    {
        var f = new UpdateTestFactory();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var updater = f.BuildUpdater(readiness, serverReadyTimeout: TimeSpan.FromSeconds(1));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var output = f.Stdout.ToString();
        Assert.Contains("'mo update server' did not refresh the runner build output or runner runtime", output);
        Assert.Contains("Local runner code may now be stale relative to the updated server", output);
        Assert.DoesNotContain("all local runtime is current", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("everything is up to date", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateServer_WhenRunnerInstalled_ProvidesFollowUpRunnerRefreshCommand()
    {
        var f = new UpdateTestFactory();
        f.SeedRunnerUnit();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var updater = f.BuildUpdater(readiness, serverReadyTimeout: TimeSpan.FromSeconds(1), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var output = f.Stdout.ToString();
        Assert.Contains("To refresh the runner, run: mo update runner", output);
        Assert.Contains("mo update", output);
        Assert.DoesNotContain("No runner service is installed locally", output);
    }

    [Fact]
    public async Task UpdateServer_WhenRunnerNotInstalled_OmitsFollowUpRunnerRefreshCommand()
    {
        var f = new UpdateTestFactory();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var updater = f.BuildUpdater(readiness, serverReadyTimeout: TimeSpan.FromSeconds(1));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var output = f.Stdout.ToString();
        Assert.Contains("'mo update server' did not refresh the runner build output or runner runtime", output);
        Assert.Contains("No runner service is installed locally", output);
        Assert.DoesNotContain("To refresh the runner, run: mo update runner", output);
    }

    [Fact]
    public async Task UpdateServer_InDryRunMode_AnnouncesRunnerNotRefreshed()
    {
        var f = new UpdateTestFactory();
        f.SeedRunnerUnit();
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK), unitDir: UpdateTestFactory.UnitDir);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: true);

        Assert.Equal(0, exitCode);
        var output = f.Stdout.ToString();
        Assert.Empty(f.Commands.ExecutedCommands);
        Assert.Contains("Dry run: would execute:", output);
        Assert.Contains("'mo update server' did not refresh the runner build output or runner runtime", output);
        Assert.Contains("To refresh the runner, run: mo update runner", output);
    }

    [Fact]
    public async Task UpdateServer_WhenReadinessDoesNotBecomeReady_ReturnsFailure()
    {
        var f = new UpdateTestFactory();
        var updater = f.BuildUpdater(
            new SequenceHttpHandler(
                new ResponseSpec(HttpStatusCode.OK),
                new ResponseSpec(HttpStatusCode.InternalServerError)),
            serverReadyTimeout: TimeSpan.FromMilliseconds(250));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("Mohist readiness checks did not pass", f.Stderr.ToString());
        Assert.Contains("Last readiness error: GET / returned 500 InternalServerError", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_ReadinessChecksAssetHeadersWithoutReadingBundleBody()
    {
        var f = new UpdateTestFactory();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK, Content: new NeverCompletingContent()));
        var updater = f.BuildUpdater(readiness, serverReadyTimeout: TimeSpan.FromSeconds(1));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(["/api/health", "/", "/assets/app.js"], readiness.Paths);
    }

    [Fact]
    public void SourceCodeUpdater_DefaultsServerReadinessToIpv4Loopback()
    {
        var f = new UpdateTestFactory();
        var updater = f.BuildUpdater();

        var httpField = typeof(RuntimeConsistencyValidator)
            .GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var http = Assert.IsType<HttpClient>(httpField!.GetValue(updater.Validator));
        Assert.Equal(new Uri("http://127.0.0.1:3456"), http.BaseAddress);
    }

    [Fact]
    public async Task UpdateServer_WhenBuildFails_AbortsWithError()
    {
        var f = new UpdateTestFactory();
        f.Commands.SetNextExitCode(1);  // build fails
        var updater = f.BuildUpdater();

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Single(f.Commands.ExecutedCommands);
        Assert.Contains("Build failed", f.Stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenBuildFails_PrintsCommandOutput()
    {
        var f = new UpdateTestFactory();
        f.Commands.SetNextResult(1, "npm error EBADPLATFORM", "MSB3073");
        var updater = f.BuildUpdater();

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        var output = f.Stderr.ToString();
        Assert.Contains("npm error EBADPLATFORM", output);
        Assert.Contains("MSB3073", output);
        Assert.Contains("Build failed", output);
    }

    [Fact]
    public async Task UpdateServer_InDryRunMode_PreviewsCommands()
    {
        var f = new UpdateTestFactory();
        var updater = f.BuildUpdater();

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: true);

        Assert.Equal(0, exitCode);
        Assert.Empty(f.Commands.ExecutedCommands);
        var output = f.Stdout.ToString();
        Assert.Contains("Dry run: would execute:", output);
        Assert.DoesNotContain("git pull", output);
        Assert.Contains("dotnet build Mohist.sln", output);
        Assert.Contains("wait for /api/health, /, and referenced /assets/* response headers readiness checks", output);
    }
}
