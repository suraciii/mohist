using System.Net;
using Mohist.Cli;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public class UpdateVerifyRuntimeSpecs
{
    [Fact]
    public async Task UpdateAll_VerifyRuntime_AllChecksPass_ReportsReadyOutcome()
    {
        var tempRoot = "/mohist-tests/mohist-verify-allpass";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();
        f.SeedManagedSkillAssets();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+abc123");
        f.Commands.SetStdoutFor("git", _ => true, "abc123");
        var systemInfo = UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "abc123", runnerStatus: "active");
        var handler = SequenceHttpHandler.WithSystemInfo(systemInfo, new ResponseSpec(HttpStatusCode.OK));
        var updater = f.BuildUpdater(handler);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        var output = f.Stdout.ToString();
        Assert.Contains("Verifying workflow runtime", output);
        Assert.Contains("Update complete. Mohist is ready.", output);
        Assert.DoesNotContain("recovered with warnings", output);
        Assert.DoesNotContain("not fully usable", output);
    }

    [Fact]
    public async Task UpdateAll_VerifyRuntime_ServerIdentityMismatch_ReportsRecoveredWithWarnings()
    {
        var tempRoot = "/mohist-tests/mohist-verify-identity";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+oldhash");
        f.Commands.SetStdoutFor("git", _ => true, "newhash");
        var systemInfo = UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "oldhash");
        var updater = f.BuildUpdater(SequenceHttpHandler.WithSystemInfo(systemInfo, new ResponseSpec(HttpStatusCode.OK)));

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        var output = f.Stdout.ToString();
        Assert.Contains("Verifying workflow runtime", output);
        Assert.Contains("recovered with warnings", output);
        Assert.Contains("Server identity", output);
        Assert.Contains("does not match source HEAD", output);
        Assert.DoesNotContain("not fully usable", output);
    }

    [Fact]
    public async Task UpdateAll_VerifyRuntime_RunnerUnavailable_ReportsFailedOutcome()
    {
        var tempRoot = "/mohist-tests/mohist-verify-runner";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+match");
        f.Commands.SetStdoutFor("git", _ => true, "match");
        var systemInfo = UpdateTestFactory.HealthySystemInfoJson(runnerStatus: "inactive");
        var updater = f.BuildUpdater(SequenceHttpHandler.WithSystemInfo(systemInfo, new ResponseSpec(HttpStatusCode.OK)));

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        var errOutput = f.Stderr.ToString();
        Assert.Contains("not fully usable", errOutput);
        Assert.Contains("Runner unavailable", errOutput);
        Assert.Contains("mo runner start", errOutput);
    }

    [Fact]
    public async Task UpdateAll_VerifyRuntime_SkillAssetsMissing_ReportsRecoveredWithWarnings()
    {
        var tempRoot = "/mohist-tests/mohist-verify-skills";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+match");
        f.Commands.SetStdoutFor("git", _ => true, "match");
        var systemInfo = UpdateTestFactory.HealthySystemInfoJson();
        var emptyHome = "/mohist-tests/mohist-verify-skills-home";
        f.Files.AddDirectory(emptyHome);
        var updater = f.BuildUpdater(
            SequenceHttpHandler.WithSystemInfo(systemInfo, new ResponseSpec(HttpStatusCode.OK)),
            userHome: emptyHome);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        var output = f.Stdout.ToString();
        Assert.Contains("Verifying workflow runtime", output);
        Assert.Contains("recovered with warnings", output);
        Assert.Contains("Managed skill assets", output);
        Assert.DoesNotContain("not fully usable", output);
    }

    [Fact]
    public async Task UpdateAll_VerifyRuntime_WebAssetsUnavailable_ReportsFailedOutcome()
    {
        var tempRoot = "/mohist-tests/mohist-verify-webassets";
        var f = new UpdateTestFactory(tempRoot);
        f.SeedPackagedSkillAssets();

        f.Commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        f.Commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+match");
        f.Commands.SetStdoutFor("git", _ => true, "match");
        // System info is healthy; readiness passes; verification GET /
        // returns 500. The verification stage should fail with web asset
        // unavailability.
        var systemInfo = UpdateTestFactory.HealthySystemInfoJson();
        var handler = SequenceHttpHandler.WithSystemInfo(
            systemInfo,
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.InternalServerError));
        var updater = f.BuildUpdater(handler);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        // Verification detects web assets unavailability; the outcome is
        // failed with the Web assets capability reported.
        Assert.Equal(1, exitCode);
        var errOutput = f.Stderr.ToString();
        Assert.Contains("not fully usable", errOutput);
        Assert.Contains("Web assets", errOutput);
    }

    [Fact]
    public async Task CheckCliBinary_WhenMoVersionSucceeds_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-cli-pass";
        var f = new UpdateTestFactory(tempRoot);
        f.Commands.SetStdoutFor("/usr/local/bin/mo", _ => true, "mo 1.0.0+abc");
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckCliBinaryAsync(context, CancellationToken.None);

        Assert.Equal("CLI binary", result.Component);
        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
        Assert.Contains("mo 1.0.0+abc", result.Message);
    }

    [Fact]
    public async Task CheckCliBinary_WhenMoVersionFails_ReportsFail()
    {
        var tempRoot = "/mohist-tests/mohist-check-cli-fail";
        var f = new UpdateTestFactory(tempRoot);
        f.Commands.SetExitCodeFor("/usr/local/bin/mo", _ => true, 1);
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckCliBinaryAsync(context, CancellationToken.None);

        Assert.Equal("CLI binary", result.Component);
        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
    }

    [Fact]
    public async Task CheckCliBinary_WhenCliPathMissing_ReportsFail()
    {
        var tempRoot = "/mohist-tests/mohist-check-cli-missing";
        var f = new UpdateTestFactory(tempRoot);
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: null, CancellationToken.None);

        var result = await updater.Validator.CheckCliBinaryAsync(context, CancellationToken.None);

        Assert.Equal("CLI binary", result.Component);
        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("not resolved", result.Message);
    }

    [Fact]
    public async Task CheckServerIdentity_WhenHashesMatch_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-identity-pass";
        var f = new UpdateTestFactory(tempRoot);
        f.Commands.SetStdoutFor("git", _ => true, "abc123");
        var updater = f.BuildUpdater(SequenceHttpHandler.WithSystemInfo(UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "abc123"), new ResponseSpec(HttpStatusCode.OK)));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckServerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CheckServerIdentity_WhenHashesMismatch_ReportsWarn()
    {
        var tempRoot = "/mohist-tests/mohist-check-identity-warn";
        var f = new UpdateTestFactory(tempRoot);
        f.Commands.SetStdoutFor("git", _ => true, "newhash");
        var updater = f.BuildUpdater(SequenceHttpHandler.WithSystemInfo(UpdateTestFactory.HealthySystemInfoJson(runningGitHash: "oldhash"), new ResponseSpec(HttpStatusCode.OK)));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckServerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Contains("does not match source HEAD", result.Message);
    }

    [Fact]
    public async Task CheckRunnerConnection_WhenRunnerActive_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-runner-pass";
        var f = new UpdateTestFactory(tempRoot);
        var updater = f.BuildUpdater(SequenceHttpHandler.WithSystemInfo(UpdateTestFactory.HealthySystemInfoJson(runnerStatus: "active"), new ResponseSpec(HttpStatusCode.OK)));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckRunnerConnectionAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CheckRunnerConnection_WhenRunnerInactive_ReportsFail()
    {
        var tempRoot = "/mohist-tests/mohist-check-runner-fail";
        var f = new UpdateTestFactory(tempRoot);
        var updater = f.BuildUpdater(SequenceHttpHandler.WithSystemInfo(UpdateTestFactory.HealthySystemInfoJson(runnerStatus: "inactive"), new ResponseSpec(HttpStatusCode.OK)));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckRunnerConnectionAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("'inactive'", result.Message);
    }

    [Fact]
    public async Task CheckManagedSkillAssets_WhenSkillFilePresent_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-skills-pass";
        var f = new UpdateTestFactory(tempRoot);
        f.Files.AddFile(
            Path.Combine(tempRoot, ".mohist", "cli", "skill-data", "mohist", "SKILL.md"),
            "---\nname: mohist\ndescription: test\n---\n\n# mohist\n");
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckManagedSkillAssetsAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CheckManagedSkillAssets_WhenSkillFilesMissing_ReportsWarn()
    {
        var tempRoot = "/mohist-tests/mohist-check-skills-warn";
        var f = new UpdateTestFactory(tempRoot);
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckManagedSkillAssetsAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task CheckWebAssets_WhenIndexAndAssetSucceed_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-web-pass";
        var f = new UpdateTestFactory(tempRoot);
        var updater = f.BuildUpdater(new SequenceHttpHandler(HttpStatusCode.OK));
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckWebAssetsAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CheckWebAssets_WhenIndexFails_ReportsFail()
    {
        var tempRoot = "/mohist-tests/mohist-check-web-fail";
        var f = new UpdateTestFactory(tempRoot);
        var handler = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.InternalServerError));
        handler.SetSystemInfoJson(null);
        var updater = f.BuildUpdater(handler);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckWebAssetsAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("500", result.Message);
    }
}
