using Xunit;
using Mohist.Cli;
using System.Net;
using System.Text.Json;
using EnvironmentAbstractions.TestHelpers;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class UpdateTests
{
    [Fact]
    public async Task UpdateAll_UpdatesCliThenContinuesWithRefreshedProcess()
    {
        var tempRoot = "/mohist-tests/mohist-update-all";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteRunnerUnit(files, "/units");


        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor(
            "/home/user/.local/bin/mo",
            args => args.SequenceEqual([
                "update",
                "--continue-after-cli-update",
                "--cli-path",
                "/home/user/.local/bin/mo",
                "--repo-root",
                tempRoot,
            ]),
            "continued update output\n");
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var http = new HttpClient(SequenceHttpHandler.WithSystemInfo(HealthySystemInfoJson(runningGitHash: "testsha"), new ResponseSpec(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("http://localhost:3456"),
        };
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            http,
            getUserHome: () => tempRoot,
            unitDir: "/units");

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo");

        var explicitCli = "/home/user/.local/bin/mo";
        var wrapper = Path.Combine(tempRoot, ".local", "bin", "mo").Replace('\\', '/');
        Assert.Equal(0, exitCode);
        Assert.Equal("dotnet", commands.ExecutedCommands[0].FileName);
        Assert.Equal("publish", commands.ExecutedCommands[0].Args[0]);
        Assert.Equal("cp", commands.ExecutedCommands[1].FileName);
        Assert.Equal(explicitCli + ".tmp", commands.ExecutedCommands[1].Args[1]);
        Assert.Equal("chmod", commands.ExecutedCommands[2].FileName);
        Assert.Equal("mv", commands.ExecutedCommands[3].FileName);
        Assert.Equal(explicitCli, commands.ExecutedCommands[3].Args[1]);
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "chmod" && c.Args.SequenceEqual(["+x", wrapper]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == explicitCli
            && c.WorkingDirectory == tempRoot
            && c.Args.SequenceEqual([
                "update",
                "--continue-after-cli-update",
                "--cli-path",
                explicitCli,
                "--repo-root",
                tempRoot,
            ]));
        Assert.DoesNotContain(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(commands.ExecutedCommands, c =>
            c.FileName == "dotnet" && c.Args.SequenceEqual(["build", "Mohist.sln"]));
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "npm");
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "git" && c.Args.SequenceEqual(["pull"]));
        AssertManagedSkillAssetsSynced(files, tempRoot);
    }

    [Fact]
    public async Task UpdateAll_WhenContinuingAfterCliUpdate_UpdatesServerAndRunnerWithoutPulling()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-continue";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteRunnerUnit(files, "/units");

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+testsha");
        commands.SetStdoutFor("git", _ => true, "testsha");
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var http = new HttpClient(SequenceHttpHandler.WithSystemInfo(HealthySystemInfoJson(runningGitHash: "testsha"), new ResponseSpec(HttpStatusCode.OK)))
        {
            BaseAddress = new Uri("http://localhost:3456"),
        };
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            http,
            getUserHome: () => tempRoot,
            unitDir: "/units");

        var exitCode = await updater.UpdateAllAsync(
            tempRoot,
            dryRun: false,
            cliPath: "/home/user/.local/bin/mo",
            continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c =>
            c.FileName == "dotnet" && c.Args.Length > 0 && c.Args[0] == "publish");
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "dotnet" && c.Args.SequenceEqual(["build", "Mohist.sln"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "restart", "mohist.service"]));
        Assert.Contains(commands.ExecutedCommands, c => c.FileName == "npm");
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "git" && c.Args.SequenceEqual(["rev-parse", "HEAD"]));
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "git" && c.Args.SequenceEqual(["pull"]));
    }

    [Fact]
    public async Task UpdateAll_WhenRunnerNotInstalled_SkipsRunnerRefreshAfterServerUpdate()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-no-runner";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));

        var commands = new FakeCommandExecutor();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "dotnet" && c.Args.SequenceEqual(["build", "Mohist.sln"]));
        Assert.DoesNotContain(commands.ExecutedCommands, c => c.FileName == "npm");
        var output = stdout.ToString();
        Assert.Contains("Runner service is not installed; skipping pre-server runner stop.", output);
        Assert.Contains("Runner refresh skipped: runner service is not installed", output);
        Assert.Contains("runner-refresh-skipped(runner service is not installed)", output);
    }

    [Fact]
    public async Task UpdateCli_PublishesAndReplacesResolvedMoBinary()
    {
        var tempRoot = "/mohist-tests/mohist-update-cli";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));


        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot,
            unitDir: "/units");

        var exitCode = await updater.UpdateCliAsync(tempRoot, dryRun: false);

        var managedCli = Path.Combine(tempRoot, ".local", "share", "mohist", "cli", "mo").Replace('\\', '/');
        var wrapper = Path.Combine(tempRoot, ".local", "bin", "mo").Replace('\\', '/');
        Assert.Equal(0, exitCode);
        Assert.Equal("dotnet", commands.ExecutedCommands[0].FileName);
        Assert.Equal("publish", commands.ExecutedCommands[0].Args[0]);
        Assert.Equal("cp", commands.ExecutedCommands[1].FileName);
        Assert.Equal(managedCli + ".tmp", commands.ExecutedCommands[1].Args[1]);
        Assert.Equal("chmod", commands.ExecutedCommands[2].FileName);
        Assert.Equal("mv", commands.ExecutedCommands[3].FileName);
        Assert.Equal(managedCli, commands.ExecutedCommands[3].Args[1]);
        Assert.Equal("chmod", commands.ExecutedCommands[4].FileName);
        Assert.Equal("+x", commands.ExecutedCommands[4].Args[0]);
        Assert.Equal(wrapper + ".tmp", commands.ExecutedCommands[4].Args[1]);
        Assert.Equal($"#!/bin/sh{Environment.NewLine}exec \"{managedCli}\" \"$@\"{Environment.NewLine}", files.ReadAllText(wrapper));
        AssertManagedSkillAssetsSynced(files, tempRoot);
    }

    [Fact]
    public async Task UpdateServer_BuildsCurrentSourceAndRestarts()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            });

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, commands.ExecutedCommands.Count);
        Assert.Equal("dotnet", commands.ExecutedCommands[0].FileName);
        Assert.Equal(new[] { "build", "Mohist.sln" }, commands.ExecutedCommands[0].Args);
        Assert.Equal("/repo", commands.ExecutedCommands[0].WorkingDirectory);
        Assert.Equal("systemctl", commands.ExecutedCommands[1].FileName);
        Assert.Equal(new[] { "--user", "restart", "mohist.service" }, commands.ExecutedCommands[1].Args);
    }

    [Fact]
    public async Task UpdateServer_WaitsForReadinessAfterRestart()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var readiness = new SequenceHttpHandler(
            null,
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromSeconds(30));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, readiness.Requests);
        Assert.Equal(["/api/health", "/api/health", "/", "/assets/app.js"], readiness.Paths);
        Assert.Contains("Server is ready.", stdout.ToString());
    }

    [Fact]
    public async Task UpdateServer_AfterSuccess_AnnouncesRunnerNotRefreshed()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromSeconds(1));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("'mo update server' did not refresh the runner build output or runner runtime", output);
        Assert.Contains("Local runner code may now be stale relative to the updated server", output);
        Assert.DoesNotContain("all local runtime is current", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("everything is up to date", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateServer_WhenRunnerInstalled_ProvidesFollowUpRunnerRefreshCommand()
    {
        var files = new FakeFileSystem();
        WriteRunnerUnit(files, "/units");
        var commands = new FakeCommandExecutor();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromSeconds(1),
            unitDir: "/units");

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("To refresh the runner, run: mo update runner", output);
        Assert.Contains("mo update", output);
        Assert.DoesNotContain("No runner service is installed locally", output);
    }

    [Fact]
    public async Task UpdateServer_WhenRunnerNotInstalled_OmitsFollowUpRunnerRefreshCommand()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK));
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromSeconds(1));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("'mo update server' did not refresh the runner build output or runner runtime", output);
        Assert.Contains("No runner service is installed locally", output);
        Assert.DoesNotContain("To refresh the runner, run: mo update runner", output);
    }

    [Fact]
    public async Task UpdateServer_InDryRunMode_AnnouncesRunnerNotRefreshed()
    {
        var files = new FakeFileSystem();
        WriteRunnerUnit(files, "/units");
        var commands = new FakeCommandExecutor();
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            unitDir: "/units");

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: true);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Empty(commands.ExecutedCommands);
        Assert.Contains("Dry run: would execute:", output);
        Assert.Contains("'mo update server' did not refresh the runner build output or runner runtime", output);
        Assert.Contains("To refresh the runner, run: mo update runner", output);
    }

    [Fact]
    public async Task UpdateServer_WhenReadinessDoesNotBecomeReady_ReturnsFailure()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(
                new ResponseSpec(HttpStatusCode.OK),
                new ResponseSpec(HttpStatusCode.InternalServerError)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromMilliseconds(250));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("Mohist readiness checks did not pass", stderr.ToString());
        Assert.Contains("Last readiness error: GET / returned 500 InternalServerError", stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_ReadinessChecksAssetHeadersWithoutReadingBundleBody()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK, Content: new NeverCompletingContent()));
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromSeconds(1));

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Equal(["/api/health", "/", "/assets/app.js"], readiness.Paths);
    }

    [Fact]
    public void SourceCodeUpdater_DefaultsServerReadinessToIpv4Loopback()
    {
        var environment = new MockEnvironmentVariableProvider();
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            environment);

        var httpField = typeof(RuntimeConsistencyValidator)
            .GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var http = Assert.IsType<HttpClient>(httpField!.GetValue(updater.Validator));
        Assert.Equal(new Uri("http://127.0.0.1:3456"), http.BaseAddress);
    }

    [Fact]
    public async Task UpdateRunner_BuildsCurrentSourceAndRestarts()
    {
        var files = new FakeFileSystem();
        WriteRunnerUnit(files, "/units");
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(new ResponseSpec(HttpStatusCode.OK, BuildRunnerIdentityResponse("runner-1", Environment.MachineName, null, "online"), "application/json")))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            unitDir: "/units");

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var npm = Assert.Single(commands.ExecutedCommands, c => c.FileName == "npm");
        Assert.Equal(new[] { "run", "build", "-w", "packages/runner" }, npm.Args);
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(new[] { "--user", "restart", "mohist-runner.service" }));
    }

    [Fact]
    public async Task UpdateRunner_WhenRunnerNotInstalled_SkipsWithReason()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(stdout, stderr, files, commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            unitDir: "/units");

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        Assert.Contains("Runner refresh skipped: runner service is not installed", stdout.ToString());
        Assert.Contains("runner-refresh-skipped(runner service is not installed)", stdout.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenIdentityMatchesRepoHead_ReportsCurrent()
    {
        var files = new FakeFileSystem();
        WriteRunnerUnit(files, "/units");
        var hash = "abcdef1234567890abcdef1234567890abcdef12";
        var commands = new FakeCommandExecutor();
        commands.SetResultFor("git", args => args.SequenceEqual(new[] { "rev-parse", "HEAD" }), 0, hash + "\n", "");
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var identityResponse = BuildRunnerIdentityResponse("runner-1", "test-host", hash, "online");
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(new ResponseSpec(HttpStatusCode.OK, identityResponse, "application/json")))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            unitDir: "/units",
            runnerIdentityTimeout: TimeSpan.FromMilliseconds(2000),
            getLocalHostname: () => "test-host");

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, exitCode);
        var actual = stdout.ToString();
        Assert.Contains("Runner runtime verification: current", actual);
    }

    [Fact]
    public async Task UpdateRunner_WhenIdentityDiffersFromRepoHead_ReportsStaleRuntime()
    {
        var files = new FakeFileSystem();
        WriteRunnerUnit(files, "/units");
        var repoHead = "0123456789abcdef0123456789abcdef01234567";
        var staleHash = "fedcba9876543210fedcba9876543210fedcba98";
        var commands = new FakeCommandExecutor();
        commands.SetResultFor("git", args => args.SequenceEqual(new[] { "rev-parse", "HEAD" }), 0, repoHead + "\n", "");
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var identityResponse = BuildRunnerIdentityResponse("runner-1", "test-host", staleHash, "online");
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(new ResponseSpec(HttpStatusCode.OK, identityResponse, "application/json")))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            unitDir: "/units",
            runnerIdentityTimeout: TimeSpan.FromMilliseconds(2000),
            getLocalHostname: () => "test-host");

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        var output = stderr.ToString();
        Assert.Contains("stale-runner-runtime", output);
        Assert.Contains(staleHash, output);
        Assert.Contains(repoHead, output);
    }

    [Fact]
    public async Task UpdateRunner_WhenRunnerDoesNotReconnect_ReportsNotReconnectedEvenWhenBuildInfoMatches()
    {
        var files = new FakeFileSystem();
        WriteRunnerUnit(files, "/units");
        var hash = "9999888877776666555544443333222211110000";
        files.AddDirectory("/repo/packages/runner/dist");
        files.AddFile("/repo/packages/runner/dist/build-info.json", $"{{\"gitHash\":\"{hash}\",\"builtAt\":1700000000}}");
        var commands = new FakeCommandExecutor();
        commands.SetResultFor("git", args => args.SequenceEqual(new[] { "rev-parse", "HEAD" }), 0, hash + "\n", "");
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.NotFound))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            unitDir: "/units",
            runnerIdentityTimeout: TimeSpan.FromMilliseconds(100),
            getLocalHostname: () => "test-host");

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Contains("runner-not-reconnected", stderr.ToString());
    }

    [Fact]
    public async Task UpdateRunner_WhenRunnerDoesNotReconnectAndBuildInfoStale_ReportsStaleRuntime()
    {
        var files = new FakeFileSystem();
        WriteRunnerUnit(files, "/units");
        var repoHead = "1111222233334444555566667777888899990000";
        var staleHash = "aaaa1111bbbb2222cccc3333dddd4444eeee5555";
        files.AddDirectory("/repo/packages/runner/dist");
        files.AddFile("/repo/packages/runner/dist/build-info.json", $"{{\"gitHash\":\"{staleHash}\",\"builtAt\":1700000000}}");
        var commands = new FakeCommandExecutor();
        commands.SetResultFor("git", args => args.SequenceEqual(new[] { "rev-parse", "HEAD" }), 0, repoHead + "\n", "");
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.NotFound))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            unitDir: "/units",
            runnerIdentityTimeout: TimeSpan.FromMilliseconds(100),
            getLocalHostname: () => "test-host");

        var exitCode = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        var actual = stderr.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("runner-not-reconnected", actual);
    }

    [Fact]
    public async Task UpdateAll_WhenServerUpdateFailsAfterStoppingRunner_RestoresRunner()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-fail1";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteRunnerUnit(files, "/units");


        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetExitCodeFor("dotnet", args => args.Length > 0 && args[0] == "build", 1);
        var stderr = new StringWriter();
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot,
            unitDir: "/units");

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
        Assert.Contains("Restoring workflow runner", stdout.ToString());
    }

    [Fact]
    public async Task UpdateAll_WhenServerUpdateFailsAfterStoppingRunnerAndRunnerWasNotRunning_DoesNotRestoreRunner()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-fail1b";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteRunnerUnit(files, "/units");

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "inactive\n");
        commands.SetExitCodeFor("dotnet", args => args.Length > 0 && args[0] == "build", 1);
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot,
            unitDir: "/units");

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "is-active", "mohist-runner.service"]));
        Assert.DoesNotContain(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenReadinessTimeoutAfterStoppingRunner_RestoresRunner()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-timeout";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteRunnerUnit(files, "/units");

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        var stderr = new StringWriter();
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.ServiceUnavailable));
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromMilliseconds(150),
            getUserHome: () => tempRoot,
            unitDir: "/units");

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
        Assert.Contains("Restoring workflow runner", stdout.ToString());
    }

    [Fact]
    public async Task UpdateAll_WhenInterruptedAfterRunnerStop_RestoresRunner()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-ctrlc";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteRunnerUnit(files, "/units");

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(
                new ResponseSpec(HttpStatusCode.ServiceUnavailable)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromSeconds(10),
            getUserHome: () => tempRoot,
            unitDir: "/units");

        using var cts = new CancellationTokenSource();
        commands.OnExecute = (fileName, args) =>
        {
            if (fileName == "systemctl" && args.SequenceEqual(["--user", "stop", "mohist-runner.service"]))
                cts.Cancel();
        };
        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", cts.Token, continueAfterCliUpdate: true);

        Assert.Equal(130, exitCode);
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.Contains(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenInterruptedBeforeRunnerStop_ExitsCleanlyWithoutRestoringRunner()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-early-cancel";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        var stderr = new StringWriter();
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot,
            unitDir: "/units");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", cts.Token, continueAfterCliUpdate: true);

        Assert.Equal(130, exitCode);
        Assert.Contains("No recovery needed", stdout.ToString());
        Assert.DoesNotContain(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "stop", "mohist-runner.service"]));
        Assert.DoesNotContain(commands.ExecutedCommands, c =>
            c.FileName == "systemctl" && c.Args.SequenceEqual(["--user", "start", "mohist-runner.service"]));
    }

    [Fact]
    public async Task UpdateAll_WhenRunnerRestoreFails_ReportsUnavailableCapabilityAndManualCommand()
    {
        var tempRoot = "/mohist-tests/mohist-update-all-restore-fail";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteRunnerUnit(files, "/units");

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetExitCodeFor("systemctl", args => args.Length >= 2 && args[1] == "start", 1);
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var readiness = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.ServiceUnavailable));
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(readiness)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            TimeSpan.FromMilliseconds(150),
            getUserHome: () => tempRoot,
            unitDir: "/units");

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("Runner unavailable", stderr.ToString());
        Assert.Contains("mo runner start", stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenBuildFails_AbortsWithError()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextExitCode(1);  // build fails
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        Assert.Single(commands.ExecutedCommands);
        Assert.Contains("Build failed", stderr.ToString());
    }

    [Fact]
    public async Task UpdateServer_WhenBuildFails_PrintsCommandOutput()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextResult(1, "npm error EBADPLATFORM", "MSB3073");
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: false);

        Assert.Equal(1, exitCode);
        var output = stderr.ToString();
        Assert.Contains("npm error EBADPLATFORM", output);
        Assert.Contains("MSB3073", output);
        Assert.Contains("Build failed", output);
    }

    [Fact]
    public async Task UpdateCli_WhenPublishFails_PrintsCommandOutput()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetNextResult(1, "publish stdout", "publish stderr");
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            stderr,
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            stderr,
            installer,
            commands);

        var exitCode = await updater.UpdateCliAsync("/repo", dryRun: false, cliPath: "/home/user/.local/bin/mo");

        Assert.Equal(1, exitCode);
        var output = stderr.ToString();
        Assert.Contains("publish stdout", output);
        Assert.Contains("publish stderr", output);
        Assert.Contains("CLI publish failed", output);
    }

    [Fact]
    public async Task UpdateServer_InDryRunMode_PreviewsCommands()
    {
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var stdout = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            new StringWriter(),
            installer,
            commands);

        var exitCode = await updater.UpdateServerAsync("/repo", dryRun: true);

        Assert.Equal(0, exitCode);
        Assert.Empty(commands.ExecutedCommands);
        var output = stdout.ToString();
        Assert.Contains("Dry run: would execute:", output);
        Assert.DoesNotContain("git pull", output);
        Assert.Contains("dotnet build Mohist.sln", output);
        Assert.Contains("wait for /api/health, /, and referenced /assets/* response headers readiness checks", output);
    }

    [Fact]
    public async Task UpdateAll_VerifyRuntime_AllChecksPass_ReportsReadyOutcome()
    {
        var tempRoot = "/mohist-tests/mohist-verify-allpass";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteManagedSkillAssets(files, tempRoot);

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+abc123");
        commands.SetStdoutFor("git", _ => true, "abc123");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var systemInfo = HealthySystemInfoJson(runningGitHash: "abc123", runnerStatus: "active");
        var handler = SequenceHttpHandler.WithSystemInfo(systemInfo, new ResponseSpec(HttpStatusCode.OK));
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Verifying workflow runtime", output);
        Assert.Contains("Update complete. Mohist is ready.", output);
        Assert.DoesNotContain("recovered with warnings", output);
        Assert.DoesNotContain("not fully usable", output);
    }

    [Fact]
    public async Task UpdateAll_VerifyRuntime_ServerIdentityMismatch_ReportsRecoveredWithWarnings()
    {
        var tempRoot = "/mohist-tests/mohist-verify-identity";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+oldhash");
        commands.SetStdoutFor("git", _ => true, "newhash");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var systemInfo = HealthySystemInfoJson(runningGitHash: "oldhash");
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(SequenceHttpHandler.WithSystemInfo(systemInfo, new ResponseSpec(HttpStatusCode.OK)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
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
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+match");
        commands.SetStdoutFor("git", _ => true, "match");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var systemInfo = HealthySystemInfoJson(runnerStatus: "inactive");
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(SequenceHttpHandler.WithSystemInfo(systemInfo, new ResponseSpec(HttpStatusCode.OK)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(1, exitCode);
        var errOutput = stderr.ToString();
        Assert.Contains("not fully usable", errOutput);
        Assert.Contains("Runner unavailable", errOutput);
        Assert.Contains("mo runner start", errOutput);
    }

    [Fact]
    public async Task UpdateAll_VerifyRuntime_SkillAssetsMissing_ReportsRecoveredWithWarnings()
    {
        var tempRoot = "/mohist-tests/mohist-verify-skills";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+match");
        commands.SetStdoutFor("git", _ => true, "match");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var systemInfo = HealthySystemInfoJson();
        var emptyHome = "/mohist-tests/mohist-verify-skills-home";
        files.AddDirectory(emptyHome);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(SequenceHttpHandler.WithSystemInfo(systemInfo, new ResponseSpec(HttpStatusCode.OK)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => emptyHome);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Verifying workflow runtime", output);
        Assert.Contains("recovered with warnings", output);
        Assert.Contains("Managed skill assets", output);
        Assert.DoesNotContain("not fully usable", output);
    }

    [Fact]
    public async Task UpdateAll_VerifyRuntime_WebAssetsUnavailable_ReportsFailedOutcome()
    {
        var tempRoot = "/mohist-tests/mohist-verify-webassets";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+match");
        commands.SetStdoutFor("git", _ => true, "match");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        // System info is healthy; readiness passes; verification GET /
        // returns 500. The verification stage should fail with web asset
        // unavailability.
        var systemInfo = HealthySystemInfoJson();
        var handler = SequenceHttpHandler.WithSystemInfo(
            systemInfo,
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"),
            new ResponseSpec(HttpStatusCode.OK),
            new ResponseSpec(HttpStatusCode.InternalServerError));
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        // Verification detects web assets unavailability; the outcome is
        // failed with the Web assets capability reported.
        Assert.Equal(1, exitCode);
        var errOutput = stderr.ToString();
        Assert.Contains("not fully usable", errOutput);
        Assert.Contains("Web assets", errOutput);
    }

    [Fact]
    public async Task CheckCliBinary_WhenMoVersionSucceeds_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-cli-pass";
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("/usr/local/bin/mo", _ => true, "mo 1.0.0+abc");
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
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
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetExitCodeFor("/usr/local/bin/mo", _ => true, 1);
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckCliBinaryAsync(context, CancellationToken.None);

        Assert.Equal("CLI binary", result.Component);
        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
    }

    [Fact]
    public async Task CheckCliBinary_WhenCliPathMissing_ReportsFail()
    {
        var tempRoot = "/mohist-tests/mohist-check-cli-missing";
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
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
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("git", _ => true, "abc123");
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(SequenceHttpHandler.WithSystemInfo(HealthySystemInfoJson(runningGitHash: "abc123"), new ResponseSpec(HttpStatusCode.OK)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckServerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CheckServerIdentity_WhenHashesMismatch_ReportsWarn()
    {
        var tempRoot = "/mohist-tests/mohist-check-identity-warn";
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("git", _ => true, "newhash");
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(SequenceHttpHandler.WithSystemInfo(HealthySystemInfoJson(runningGitHash: "oldhash"), new ResponseSpec(HttpStatusCode.OK)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckServerIdentityAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Contains("does not match source HEAD", result.Message);
    }

    [Fact]
    public async Task CheckRunnerConnection_WhenRunnerActive_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-runner-pass";
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(SequenceHttpHandler.WithSystemInfo(HealthySystemInfoJson(runnerStatus: "active"), new ResponseSpec(HttpStatusCode.OK)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckRunnerConnectionAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CheckRunnerConnection_WhenRunnerInactive_ReportsFail()
    {
        var tempRoot = "/mohist-tests/mohist-check-runner-fail";
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(SequenceHttpHandler.WithSystemInfo(HealthySystemInfoJson(runnerStatus: "inactive"), new ResponseSpec(HttpStatusCode.OK)))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckRunnerConnectionAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("'inactive'", result.Message);
    }

    [Fact]
    public async Task CheckManagedSkillAssets_WhenSkillFilePresent_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-skills-pass";
        var files = new FakeFileSystem();
        files.AddFile(
            Path.Combine(tempRoot, ".mohist", "cli", "skill-data", "mohist", "SKILL.md"),
            "---\nname: mohist\ndescription: test\n---\n\n# mohist\n");
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckManagedSkillAssetsAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CheckManagedSkillAssets_WhenSkillFilesMissing_ReportsWarn()
    {
        var tempRoot = "/mohist-tests/mohist-check-skills-warn";
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckManagedSkillAssetsAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Warn, result.Outcome);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task CheckWebAssets_WhenIndexAndAssetSucceed_ReportsPass()
    {
        var tempRoot = "/mohist-tests/mohist-check-web-pass";
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(new SequenceHttpHandler(HttpStatusCode.OK))
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckWebAssetsAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CheckWebAssets_WhenIndexFails_ReportsFail()
    {
        var tempRoot = "/mohist-tests/mohist-check-web-fail";
        var files = new FakeFileSystem();
        var commands = new FakeCommandExecutor();
        var installer = new SystemdServiceInstaller(
            new StringWriter(),
            new StringWriter(),
            files,
            commands);
        var handler = new SequenceHttpHandler(
            new ResponseSpec(HttpStatusCode.InternalServerError));
        handler.SetSystemInfoJson(null);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            new StringWriter(),
            new StringWriter(),
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);
        var context = new UpdateContext(dryRun: false, repoRoot: tempRoot, cliPath: "/usr/local/bin/mo", CancellationToken.None);

        var result = await updater.Validator.CheckWebAssetsAsync(context, CancellationToken.None);

        Assert.Equal(RuntimeCheckOutcome.Fail, result.Outcome);
        Assert.Contains("500", result.Message);
    }

    private static string HealthySystemInfoJson(string runningGitHash = "abc123", string runnerStatus = "active")
    {
        return $"{{\"success\":true,\"data\":{{\"running\":{{\"gitHash\":\"{runningGitHash}\"}},\"services\":{{\"runner\":\"{runnerStatus}\"}}}}}}";
    }

    private static string ExtractRunningGitHash(string systemInfoJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(systemInfoJson);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("running", out var running)
                && running.TryGetProperty("gitHash", out var gitHash)
                && gitHash.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return gitHash.GetString() ?? "unknown";
            }
        }
        catch
        {
        }
        return "unknown";
    }

    [Fact]
    public async Task UpdateAll_WhenServerReachable_PostsOutcomeToServer()
    {
        var tempRoot = "/mohist-tests/mohist-outcome-posted";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteManagedSkillAssets(files, tempRoot);

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+abc123");
        commands.SetStdoutFor("git", _ => true, "abc123");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var systemInfo = HealthySystemInfoJson(runningGitHash: "abc123", runnerStatus: "active");
        var handler = new OutcomeCapturingHttpHandler(systemInfo);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

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
        Assert.Contains("Update outcome persisted to server.", stdout.ToString());
    }

    [Fact]
    public async Task UpdateAll_WhenServerUnreachable_SkipsOutcomePostWithMessage()
    {
        var tempRoot = "/mohist-tests/mohist-outcome-unreachable";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteManagedSkillAssets(files, tempRoot);

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+abc123");
        commands.SetStdoutFor("git", _ => true, "abc123");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var systemInfo = HealthySystemInfoJson(runningGitHash: "abc123", runnerStatus: "active");
        var handler = new OutcomeCapturingHttpHandler(systemInfo)
        {
            OutcomeResponseStatusCode = HttpStatusCode.ServiceUnavailable,
        };
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", continueAfterCliUpdate: true);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Update complete. Mohist is ready.", output);
        Assert.Contains("Could not persist update outcome to server", output);
        Assert.DoesNotContain("Update outcome persisted to server.", output);
    }

    [Fact]
    public async Task UpdateAll_WhenInterruptedBeforeRunnerStop_DoesNotPostOutcomeToServer()
    {
        var tempRoot = "/mohist-tests/mohist-cancel-no-post";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));

        var commands = new FakeCommandExecutor();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var handler = new OutcomeCapturingHttpHandler(HealthySystemInfoJson());
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exitCode = await updater.UpdateAllAsync(tempRoot, dryRun: false, cliPath: "/home/user/.local/bin/mo", cts.Token, continueAfterCliUpdate: true);

        Assert.Equal(130, exitCode);
        Assert.Contains("No recovery needed", stdout.ToString());
        Assert.Contains("no outcome was posted", stdout.ToString());
        Assert.Null(handler.LastOutcomeRequest);
    }

    [Fact]
    public async Task UpdateAll_WebUiCanReadCliOutcomeViaStatusEndpoint()
    {
        var tempRoot = "/mohist-tests/mohist-outcome-webui";
        var files = new FakeFileSystem();
        WritePackagedSkillAssets(files, Path.Combine(tempRoot, ".publish", "cli", "skill-data"));
        WriteManagedSkillAssets(files, tempRoot);

        var commands = new FakeCommandExecutor();
        commands.SetStdoutFor("systemctl", args => args.Length >= 3 && args[1] == "is-active", "active\n");
        commands.SetStdoutFor("/home/user/.local/bin/mo", _ => true, "1.0.0+abc123");
        commands.SetStdoutFor("git", _ => true, "abc123");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var installer = new SystemdServiceInstaller(
            stdout,
            stderr,
            files,
            commands);
        var systemInfo = HealthySystemInfoJson(runningGitHash: "abc123", runnerStatus: "active");
        var handler = new OutcomeCapturingHttpHandler(systemInfo);
        var updater = SourceCodeUpdater.CreateWithDefaults(
            stdout,
            stderr,
            installer,
            commands,
            files,
            new MockEnvironmentVariableProvider(),
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost:3456"),
            },
            getUserHome: () => tempRoot);

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

    private sealed class FakeFileSystem : Mohist.Server.UnitTests.Support.FakeFileSystem
    {
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public readonly List<(string FileName, string[] Args, string? WorkingDirectory)> ExecutedCommands = new();
        private readonly Queue<int> _exitCodes = new();
        private readonly Queue<string> _stdout = new();
        private readonly Queue<string> _stderr = new();
        private readonly List<(string FileName, Func<string[], bool> Match, int ExitCode)> _exitCodeRules = new();
        private readonly List<(string FileName, Func<string[], bool> Match, string Stdout)> _stdoutRules = new();
        private readonly List<(string FileName, Func<string[], bool> Match, int ExitCode, string Stdout, string Stderr)> _resultRules = new();

        public Action<string, string[]>? OnExecute { get; set; }

        public void SetNextExitCode(int code) => _exitCodes.Enqueue(code);
        public void SetNextStdout(string stdout) => _stdout.Enqueue(stdout);
        public void SetNextResult(int exitCode, string stdout, string stderr)
        {
            _exitCodes.Enqueue(exitCode);
            _stdout.Enqueue(stdout);
            _stderr.Enqueue(stderr);
        }
        public void SetExitCodeFor(string fileName, Func<string[], bool> match, int code) => _exitCodeRules.Add((fileName, match, code));
        public void SetStdoutFor(string fileName, Func<string[], bool> match, string stdout) => _stdoutRules.Add((fileName, match, stdout));
        public void SetResultFor(string fileName, Func<string[], bool> match, int exitCode, string stdout, string stderr)
            => _resultRules.Add((fileName, match, exitCode, stdout, stderr));

        public Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
            string fileName, string[] args, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            ExecutedCommands.Add((fileName, args, workingDirectory));
            OnExecute?.Invoke(fileName, args);
            var resultRule = _resultRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
            if (resultRule.Match is not null)
                return Task.FromResult((resultRule.ExitCode, resultRule.Stdout, resultRule.Stderr));
            var rule = _exitCodeRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
            var code = rule.Match is not null ? rule.ExitCode : _exitCodes.Count > 0 ? _exitCodes.Dequeue() : 0;
            var stdoutRule = _stdoutRules.FirstOrDefault(rule => rule.FileName == fileName && rule.Match(args));
            var stdout = stdoutRule.Match is not null ? stdoutRule.Stdout : _stdout.Count > 0 ? _stdout.Dequeue() : "";
            var stderr = _stderr.Count > 0 ? _stderr.Dequeue() : "";
            return Task.FromResult((code, stdout, stderr));
        }
    }

    private sealed class SequenceHttpHandler : HttpMessageHandler
    {
        private readonly ResponseSpec?[] _responses;
        private string? _systemInfoJson;

        public int Requests { get; private set; }
        public List<string> Paths { get; } = new();

        public SequenceHttpHandler(params HttpStatusCode?[] statuses)
            : this(ExpandStatusResponses(statuses))
        {
        }

        public SequenceHttpHandler(params ResponseSpec?[] responses)
            : this(responses, systemInfoJson: null)
        {
        }

        public SequenceHttpHandler(ResponseSpec?[] responses, string? systemInfoJson)
        {
            _responses = responses.Length == 0 ? [new ResponseSpec(HttpStatusCode.OK)] : responses;
            _systemInfoJson = systemInfoJson;
        }

        public static SequenceHttpHandler WithSystemInfo(string? systemInfoJson, params ResponseSpec?[] responses)
        {
            return new SequenceHttpHandler(responses, systemInfoJson);
        }

        public void SetSystemInfoJson(string? json)
        {
            _systemInfoJson = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            Paths.Add(path);

            if (string.Equals(path, "/api/system/info", StringComparison.Ordinal))
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_systemInfoJson ?? DefaultSystemInfoJson)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                    }
                });
            }

            if (_systemInfoJson is not null && path.StartsWith("/api/runner/identity", StringComparison.Ordinal))
            {
                Requests++;
                var runnerHash = ExtractRunningGitHash(_systemInfoJson);
                var identityJson = $"{{\"success\":true,\"data\":{{\"buildGitHash\":\"{runnerHash}\"}}}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(identityJson)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                    }
                });
            }

            var index = Math.Min(Requests, _responses.Length - 1);
            Requests++;
            var response = _responses[index];
            if (response is null)
                throw new HttpRequestException("server not ready");

            var message = new HttpResponseMessage(response.StatusCode);
            if (response.Body is not null)
            {
                message.Content = new StringContent(response.Body);
                if (response.ContentType is not null)
                    message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
            }
            else if (response.Content is not null)
            {
                message.Content = response.Content;
                if (response.ContentType is not null)
                    message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
            }
            else if (string.Equals(path, "/", StringComparison.Ordinal))
            {
                // Default to healthy HTML for unknown calls to /.
                message.Content = new StringContent("<html><script src=\"/assets/app.js\"></script></html>")
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") }
                };
            }
            else if (path.StartsWith("/assets/", StringComparison.Ordinal))
            {
                message.Content = new StringContent("// asset body");
            }

            return Task.FromResult(message);
        }
    }

    private const string DefaultSystemInfoJson =
        "{\"success\":true,\"data\":{\"running\":{\"gitHash\":\"testsha\"},\"services\":{\"runner\":\"active\"}}}";

    private sealed record ResponseSpec(
        HttpStatusCode StatusCode,
        string? Body = null,
        string? ContentType = null,
        HttpContent? Content = null);

    private sealed class NeverCompletingContent : HttpContent
    {
        private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _pending.Task;

        protected override bool TryComputeLength(out long length)
        {
            length = 1024 * 1024;
            return true;
        }
    }

    private static ResponseSpec?[] ExpandStatusResponses(HttpStatusCode?[] statuses)
    {
        if (statuses.Length == 0)
            statuses = [HttpStatusCode.OK];

        var expanded = new List<ResponseSpec?>();
        foreach (var response in statuses)
        {
            if (response is null)
            {
                expanded.Add(null);
                continue;
            }

            if (response.Value == HttpStatusCode.OK)
            {
                expanded.Add(new ResponseSpec(HttpStatusCode.OK));
                expanded.Add(new ResponseSpec(HttpStatusCode.OK, "<html><script src=\"/assets/app.js\"></script></html>", "text/html"));
                expanded.Add(new ResponseSpec(HttpStatusCode.OK));
                continue;
            }

            expanded.Add(new ResponseSpec(response.Value));
        }

        return expanded.ToArray();
    }

    private static void WritePackagedSkillAssets(FakeFileSystem files, string sourceRoot)
    {
        files.AddDirectory(Path.Combine(sourceRoot, "mohist"));
        files.AddDirectory(Path.Combine(sourceRoot, "mohist-explore"));
        files.AddFile(
            Path.Combine(sourceRoot, "mohist", "SKILL.md"),
            "---\nname: mohist\ndescription: test\n---\n\n# mohist\n");
        files.AddFile(
            Path.Combine(sourceRoot, "mohist-explore", "SKILL.md"),
            "---\nname: mohist-explore\ndescription: test\n---\n\n# mohist-explore\n");
    }

    private static void WriteManagedSkillAssets(FakeFileSystem files, string homeRoot)
    {
        WritePackagedSkillAssets(files, Path.Combine(homeRoot, ".mohist", "cli", "skill-data"));
    }

    private static void WriteRunnerUnit(FakeFileSystem files, string unitDir)
    {
        files.AddDirectory(unitDir);
        files.AddFile(
            Path.Combine(unitDir, "mohist-runner.service"),
            "[Unit]\nDescription=Mohist Runner\n\n[Service]\nExecStart=node packages/runner/dist/cli.js\n\n[Install]\nWantedBy=default.target\n");
    }

    private static string BuildRunnerIdentityResponse(string runnerId, string hostname, string? buildGitHash, string status)
    {
        var hash = buildGitHash is null ? "null" : $"\"{buildGitHash}\"";
        return $"{{\"success\":true,\"data\":{{\"runnerId\":\"{runnerId}\",\"hostname\":\"{hostname}\",\"buildGitHash\":{hash},\"status\":\"{status}\",\"connectionState\":\"connected\"}}}}";
    }

    private static void AssertManagedSkillAssetsSynced(FakeFileSystem files, string tempRoot)
    {
        var managedRoot = Path.Combine(tempRoot, ".mohist", "cli", "skill-data");
        Assert.True(files.HasFile(Path.Combine(managedRoot, "mohist", "SKILL.md")), "Expected mohist SKILL.md");
        Assert.True(files.HasFile(Path.Combine(managedRoot, "mohist-explore", "SKILL.md")), "Expected mohist-explore SKILL.md");
        var mohistSkillsDir = Path.Combine(tempRoot, ".mohist", "skills");
        Assert.False(files.DirectoryExists(mohistSkillsDir), "Internal .mohist/skills should remain untouched by sync");
    }

    private sealed class OutcomeCapturingHttpHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string _systemInfoJson;

        public OutcomeCapturingHttpHandler(string systemInfoJson)
        {
            _systemInfoJson = systemInfoJson;
        }

        public HttpStatusCode OutcomeResponseStatusCode { get; set; } = HttpStatusCode.OK;

        public CliOutcomeRequestPayload? LastOutcomeRequest { get; private set; }
        public List<string> Paths { get; } = new();

        public string BuildStatusResponseJson()
        {
            if (LastOutcomeRequest is null)
                throw new InvalidOperationException("No outcome request captured");

            var payload = LastOutcomeRequest;
            var response = new
            {
                jobId = payload.JobId,
                status = payload.Status,
                stage = payload.Stage,
                outcome = payload.Outcome,
                unavailableCapability = payload.UnavailableCapability,
                runningGitHash = payload.SourceHead,
                sourceHead = payload.SourceHead,
                updateAvailable = false,
                sourcePath = (string?)null,
                serverUnit = (string?)null,
                runnerUnit = (string?)null,
                reason = (string?)null,
                logs = (payload.Logs is null ? new List<CliOutcomeLogPayload>() : payload.Logs).Select(l => new
                {
                    at = l.At,
                    stage = l.Stage,
                    message = l.Message,
                }),
                createdAt = TestTime.UtcNow,
                updatedAt = TestTime.UtcNow,
                completedAt = TestTime.UtcNow,
            };
            return JsonSerializer.Serialize(response, JsonOptions);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            Paths.Add(path);

            if (string.Equals(path, "/api/system/info", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_systemInfoJson)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                    }
                };
            }

            if (path.StartsWith("/api/runner/identity", StringComparison.Ordinal))
            {
                var runnerHash = ExtractRunningGitHash(_systemInfoJson);
                var identityJson = $"{{\"success\":true,\"data\":{{\"buildGitHash\":\"{runnerHash}\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(identityJson)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                    }
                };
            }

            if (string.Equals(path, "/api/system/update/outcome", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                LastOutcomeRequest = JsonSerializer.Deserialize<CliOutcomeRequestPayload>(body, JsonOptions);
                return new HttpResponseMessage(OutcomeResponseStatusCode)
                {
                    Content = new StringContent("{\"job\":{}}")
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
                    }
                };
            }

            if (string.Equals(path, "/api/health", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (string.Equals(path, "/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><script src=\"/assets/app.js\"></script></html>")
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") }
                    }
                };
            }

            if (path.StartsWith("/assets/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("// asset body")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        public sealed class CliOutcomeRequestPayload
        {
            [System.Text.Json.Serialization.JsonPropertyName("jobId")]
            public string? JobId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("status")]
            public string? Status { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("stage")]
            public string? Stage { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("outcome")]
            public string? Outcome { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("unavailableCapability")]
            public string? UnavailableCapability { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sourceHead")]
            public string? SourceHead { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("logs")]
            public List<CliOutcomeLogPayload>? Logs { get; set; }
        }

        public sealed class CliOutcomeLogPayload
        {
            [System.Text.Json.Serialization.JsonPropertyName("at")]
            public DateTimeOffset At { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("stage")]
            public string? Stage { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("message")]
            public string? Message { get; set; }
        }
    }
}
