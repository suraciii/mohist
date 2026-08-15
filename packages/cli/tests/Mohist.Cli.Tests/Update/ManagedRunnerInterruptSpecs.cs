using System.Net;
using System.Text.Json;
using Mohist.Cli;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests.Update;

public sealed partial class ManagedRuntimeTransactionSpecs
{
    [Fact]
    public async Task ManagedRunnerUpdate_ConfirmsInterruptBeforeCandidateActivation()
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        SeedSourceRunner(fixture, "runner-pluto");
        var handler = BuildManagedRunnerHandler(fixture, interruptConfirmed: true);
        var updater = BuildManagedRunnerUpdater(fixture, handler);

        var result = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(0, result);
        Assert.Equal(
            [
                (HttpMethod.Get, "/api/runner/identity"),
                (HttpMethod.Post, "/api/runner/runner-pluto/update-interrupt"),
                (HttpMethod.Get, "/api/runner/identity"),
                (HttpMethod.Get, "/api/runner/runner-pluto/update-operation/runner-update:managed/recovery-status"),
            ],
            handler.Requests.Select(request => (request.Method, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath))));
        var runnerRestart = fixture.Commands.ExecutedCommands.FindIndex(command =>
            command.FileName == "systemctl"
            && command.Args.SequenceEqual(["--user", "restart", "mohist-runner.service"]));
        Assert.True(runnerRestart >= 0);
        Assert.Contains("MOHIST_RUNTIME_IDENTITY_PATH=", fixture.Files.Read(
            Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedRunnerUpdate_WhenActivationFails_CancelsConfirmedInterrupt()
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        SeedSourceRunner(fixture, "runner-pluto");
        var sourceUnit = fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        fixture.Commands.QueueResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "restart", "mohist-runner.service"]),
            17,
            "",
            "");
        fixture.Commands.QueueResultFor(
            "systemctl",
            args => args.SequenceEqual(["--user", "restart", "mohist-runner.service"]),
            0,
            "",
            "");
        var handler = BuildManagedRunnerHandler(fixture, interruptConfirmed: true);
        var updater = BuildManagedRunnerUpdater(fixture, handler);

        var result = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(1, result);
        Assert.Equal(sourceUnit, fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.Equal(2, fixture.Commands.ExecutedCommands.Count(command =>
            command.FileName == "systemctl"
            && command.Args.SequenceEqual(["--user", "restart", "mohist-runner.service"])));
        Assert.Equal(3, handler.Requests.Count);
        var updateInterruptId = ReadUpdateInterruptId(handler.Requests[1].Body);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal(
            $"/api/runner/runner-pluto/update-interrupt/{updateInterruptId}/cancel",
            handler.Requests[2].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ManagedRunnerUpdate_WhenInterruptIsUnconfirmed_DoesNotActivateCandidate()
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        SeedSourceRunner(fixture, "runner-pluto");
        var sourceUnit = fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        var handler = BuildManagedRunnerHandler(fixture, interruptConfirmed: false);
        var updater = BuildManagedRunnerUpdater(fixture, handler);

        var result = await updater.UpdateRunnerAsync("/repo", dryRun: false);

        Assert.Equal(1, result);
        Assert.Equal(sourceUnit, fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
        Assert.False(fixture.Files.HasFile(fixture.ActivePath));
        Assert.DoesNotContain(fixture.Commands.ExecutedCommands, command =>
            command.FileName == "systemctl"
            && command.Args.SequenceEqual(["--user", "restart", "mohist-runner.service"]));
        Assert.DoesNotContain(fixture.Files.Files.Keys, path =>
            path.Contains("/releases/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareManagedRunner_WhenInterruptPreconditionFails_RemovesUnactivatedRelease()
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        SeedSourceRunner(fixture, "runner-pluto");

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-interrupt-rejected",
            null,
            _ => Task.FromResult<string?>("runner update interrupt was not confirmed"));

        Assert.Null(prepared.Session);
        Assert.Contains("interrupt was not confirmed", prepared.Error, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Activator.ApplyCalls);
        Assert.Equal(0, fixture.Activator.RestoreCalls);
        Assert.False(fixture.Files.HasFile(fixture.ActivePath));
        Assert.DoesNotContain(fixture.Files.Files.Keys, path =>
            path.Contains("/releases/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareManagedRunner_WhenInterruptPreconditionThrows_RestoresSourceAndCleansRelease()
    {
        var fixture = ManagedFixture.Create(activationCode: 0, useSystemd: true, unitDir: UpdateTestFactory.UnitDir);
        SeedSourceRunner(fixture, "runner-pluto");

        var prepared = await fixture.Transaction.PrepareAsync(
            "/repo",
            "runner",
            "tx-runner-interrupt-exception",
            null,
            _ => Task.FromException<string?>(new InvalidOperationException("interrupt transport lost")));

        Assert.Null(prepared.Session);
        Assert.Contains("staging failed", prepared.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Activator.ApplyCalls);
        Assert.Equal(1, fixture.Activator.RestoreCalls);
        Assert.Equal("none", Parse(fixture.Files.Read(fixture.ActivePath)).Status);
        Assert.DoesNotContain(fixture.Files.Files.Keys, path =>
            path.Contains("/releases/", StringComparison.Ordinal));
    }

    private static SourceCodeUpdater BuildManagedRunnerUpdater(
        ManagedFixture fixture,
        HttpMessageHandler handler)
    {
        var systemd = fixture.Systemd
            ?? throw new InvalidOperationException("managed updater requires systemd");
        return SourceCodeUpdater.CreateWithDefaults(
            TextWriter.Null,
            TextWriter.Null,
            systemd,
            fixture.Commands,
            fixture.Files,
            fixture.Environment,
            new HttpClient(handler) { BaseAddress = new Uri(UpdateTestFactory.ServerAddress) },
            getUserHome: () => "/home/test",
            getLocalHostname: () => "pluto",
            unitDir: UpdateTestFactory.UnitDir,
            managedUpdatesEnabled: true);
    }

    private static RecordingHttpHandler BuildManagedRunnerHandler(
        ManagedFixture fixture,
        bool interruptConfirmed)
    {
        var identityReads = 0;
        string? updateInterruptId = null;
        var sourceUnit = fixture.Files.Read(Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service"));
        return new RecordingHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get
                && Uri.UnescapeDataString(path) == "/api/runner/runner-pluto/update-operation/runner-update:managed/recovery-status")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        operationId = "runner-update:managed",
                        runnerId = "runner-pluto",
                        operationStatus = "settled",
                        complete = true,
                        affectedWorks = new[]
                        {
                            new
                            {
                                ownerKind = "agent-job",
                                ownerId = "job-1",
                                workId = "agent-job-1",
                                taskRunId = (string?)null,
                                workType = "agent-job",
                                status = "receipt-acked",
                                acknowledged = true,
                            },
                        },
                    },
                }));
            }

            if (request.Method == HttpMethod.Post
                && updateInterruptId is not null
                && path == $"/api/runner/runner-pluto/update-interrupt/{updateInterruptId}/cancel")
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        runnerId = "runner-pluto",
                        updateInterruptId,
                        status = "cancelled",
                    },
                }));
            }

            if (request.Method == HttpMethod.Post
                && path == "/api/runner/runner-pluto/update-interrupt")
            {
                Assert.Equal(1, identityReads);
                Assert.False(fixture.Files.HasFile(fixture.ActivePath));
                Assert.Equal(sourceUnit, fixture.Files.Read(
                    Path.Combine(UpdateTestFactory.UnitDir, "mohist-runner.service")));
                updateInterruptId = ReadUpdateInterruptId(
                    request.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
                return Task.FromResult(interruptConfirmed
                    ? RecordingHttpHandler.Json(new
                    {
                        success = true,
                        data = new
                        {
                            runnerId = "runner-pluto",
                            status = "interrupted",
                            updateInterruptId,
                            interruptedWorkIds = new[] { "agent-job-1" },
                            interruptedWorkCount = 1,
                            operationId = "runner-update:managed",
                            affectedWorks = new[]
                            {
                                new
                                {
                                    ownerKind = "agent-job",
                                    ownerId = "job-1",
                                    workId = "agent-job-1",
                                    taskRunId = (string?)null,
                                    workType = "agent-job",
                                },
                            },
                        },
                    })
                    : RecordingHttpHandler.JsonError(
                        "runner interrupt unavailable",
                        statusCode: HttpStatusCode.ServiceUnavailable));
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/runner/identity", path);
            identityReads++;
            if (identityReads == 1)
            {
                return Task.FromResult(RecordingHttpHandler.Json(new
                {
                    success = true,
                    data = new
                    {
                        runnerId = "runner-pluto",
                        hostname = "pluto",
                        status = "online",
                        connectionState = "connected",
                    },
                }));
            }

            var active = Parse(fixture.Files.Read(fixture.ActivePath));
            var runner = Assert.IsType<RuntimeTarget>(active.Runner);
            var identity = runner.Identity;
            return Task.FromResult(RecordingHttpHandler.Json(new
            {
                success = true,
                data = new
                {
                    runnerId = identity.RunnerId,
                    hostname = "pluto",
                    buildGitHash = identity.BuildGitHash,
                    component = identity.Component,
                    version = identity.Version,
                    sourceRevision = identity.SourceRevision,
                    treeHash = identity.TreeHash,
                    artifactDigest = identity.ArtifactDigest,
                    releaseId = identity.ReleaseId,
                    generation = identity.Generation,
                    status = "online",
                    connectionState = "connected",
                    connectionGeneration = "runner-connection-1",
                },
            }));
        });
    }

    private static string ReadUpdateInterruptId(string? body)
    {
        using var document = JsonDocument.Parse(body ?? throw new InvalidOperationException(
            "update interrupt request body is required"));
        var updateInterruptId = document.RootElement.GetProperty("updateInterruptId").GetString();
        Assert.True(Guid.TryParse(updateInterruptId, out _));
        return updateInterruptId!;
    }

}
