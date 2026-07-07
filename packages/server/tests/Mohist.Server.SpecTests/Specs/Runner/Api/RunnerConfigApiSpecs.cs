using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

/// <summary>
/// Spec coverage for the dedicated runner config channel:
/// <c>GET /api/runner/{runnerId}/config</c>. The endpoint projects the
/// server's bound <see cref="CleanupPolicyOptions"/> into a
/// <see cref="RunnerConfigResponse"/> wrapper and is reachable
/// independently of <c>POST /poll</c> — i.e. the system can be fully
/// idle and the endpoint still returns <c>200 OK</c> with a body. This
/// is what breaks the "cleanupPolicy is hostage to work dispatch"
/// failure mode (issue #359).
/// </summary>
[Collection("MohistIntegration")]
public class RunnerConfigApiSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Config_ConfiguredPolicy_ProjectsAllFields()
    {
        var policy = new CleanupPolicyOptions
        {
            RetentionDays = 30,
            StorageBudgetBytes = 1_073_741_824L,
            StorageTargetWatermarkBytes = 536_870_912L,
        };
        await using var harness = await ConfigHarness.CreateAsync(policy);
        var runnerId = await harness.RegisterRunnerAsync();

        using var response = await harness.Client.GetAsync($"/api/runner/{runnerId}/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("cleanupPolicy", out var policyElement),
            "config response must contain a 'cleanupPolicy' property");
        Assert.Equal(JsonValueKind.Object, policyElement.ValueKind);
        Assert.Equal(30, policyElement.GetProperty("retentionDays").GetInt32());
        Assert.Equal(1_073_741_824L, policyElement.GetProperty("storageBudgetBytes").GetInt64());
        Assert.Equal(536_870_912L, policyElement.GetProperty("storageTargetWatermarkBytes").GetInt64());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Config_NonPositiveFields_AreEmittedAsNullSentinels()
    {
        // The "null means unlimited / disabled" contract: every field
        // is always present on the wire — configured fields carry
        // their positive value, unconfigured / non-positive fields
        // are emitted as `null`. The spec explicitly requires
        // present-null over absent so the response is
        // self-describing; the runner's CleanupPolicy TS type
        // tolerates either form, but the wire contract is locked to
        // present-null.
        var policy = new CleanupPolicyOptions
        {
            RetentionDays = 0,
            StorageBudgetBytes = -1,
            StorageTargetWatermarkBytes = null,
        };
        await using var harness = await ConfigHarness.CreateAsync(policy);
        var runnerId = await harness.RegisterRunnerAsync();

        using var response = await harness.Client.GetAsync($"/api/runner/{runnerId}/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cleanup = body.GetProperty("cleanupPolicy");
        // Every field is present; each is the JSON null literal.
        Assert.True(cleanup.TryGetProperty("retentionDays", out var retention),
            "retentionDays must be present on the wire even when null");
        Assert.Equal(JsonValueKind.Null, retention.ValueKind);
        Assert.True(cleanup.TryGetProperty("storageBudgetBytes", out var budget),
            "storageBudgetBytes must be present on the wire even when null");
        Assert.Equal(JsonValueKind.Null, budget.ValueKind);
        Assert.True(cleanup.TryGetProperty("storageTargetWatermarkBytes", out var watermark),
            "storageTargetWatermarkBytes must be present on the wire even when null");
        Assert.Equal(JsonValueKind.Null, watermark.ValueKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Config_PartiallyConfiguredPolicy_EmitsConfiguredValueAndNullsForUnsetFields()
    {
        // With one field set, that field appears with its positive
        // value; the unset ones are emitted as `null` (same
        // present-null wire convention). This is the case the runner
        // actually parses in production: a server with
        // retention-only policy is the common starting config.
        var policy = new CleanupPolicyOptions { RetentionDays = 14 };
        await using var harness = await ConfigHarness.CreateAsync(policy);
        var runnerId = await harness.RegisterRunnerAsync();

        using var response = await harness.Client.GetAsync($"/api/runner/{runnerId}/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cleanup = body.GetProperty("cleanupPolicy");
        Assert.True(cleanup.TryGetProperty("retentionDays", out var retention));
        Assert.Equal(JsonValueKind.Number, retention.ValueKind);
        Assert.Equal(14, retention.GetInt32());
        Assert.True(cleanup.TryGetProperty("storageBudgetBytes", out var budget));
        Assert.Equal(JsonValueKind.Null, budget.ValueKind);
        Assert.True(cleanup.TryGetProperty("storageTargetWatermarkBytes", out var watermark));
        Assert.Equal(JsonValueKind.Null, watermark.ValueKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Config_FullyUnconfiguredPolicy_Returns200WithAllNullFields()
    {
        // Default CleanupPolicyOptions — no fields set. The endpoint
        // must still answer 200 with a body; no body omission, no
        // error. The wrapper object is present (so the runner can
        // distinguish "policy available, no strategy enabled" from
        // "policy unavailable" / 404 / network error), and its three
        // inner fields are all present-null.
        await using var harness = await ConfigHarness.CreateAsync(new CleanupPolicyOptions());
        var runnerId = await harness.RegisterRunnerAsync();

        using var response = await harness.Client.GetAsync($"/api/runner/{runnerId}/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("cleanupPolicy", out var cleanup),
            "response body must include the 'cleanupPolicy' wrapper even when fully unconfigured");
        Assert.Equal(JsonValueKind.Object, cleanup.ValueKind);
        // All three fields present, all three null — the response is
        // self-describing: "policy present, no strategy enabled".
        var keys = cleanup.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "retentionDays", "storageBudgetBytes", "storageTargetWatermarkBytes" }, keys);
        Assert.All(new[] { "retentionDays", "storageBudgetBytes", "storageTargetWatermarkBytes" },
            key => Assert.Equal(JsonValueKind.Null, cleanup.GetProperty(key).ValueKind));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Config_IdleSystem_Returns200WithBody_IndependentOfPollState()
    {
        // The whole point of #359: the runner must be able to fetch
        // its config when no work is being dispatched. We register a
        // runner, do NOT dispatch any work, and confirm both:
        //   - /poll returns 204 No Content (idle, unchanged)
        //   - /config still returns 200 with a body
        // The two facts together prove policy availability is not
        // gated by work presence.
        var policy = new CleanupPolicyOptions { RetentionDays = 14 };
        await using var harness = await ConfigHarness.CreateAsync(policy);
        var runnerId = await harness.RegisterRunnerAsync();

        using (var poll = await harness.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, poll.StatusCode);
        }

        using var config = await harness.Client.GetAsync($"/api/runner/{runnerId}/config");

        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        var body = await config.Content.ReadFromJsonAsync<JsonElement>();
        var cleanup = body.GetProperty("cleanupPolicy");
        Assert.True(cleanup.TryGetProperty("retentionDays", out var retention));
        Assert.Equal(14, retention.GetInt32());
        Assert.True(cleanup.TryGetProperty("storageBudgetBytes", out var budget));
        Assert.Equal(JsonValueKind.Null, budget.ValueKind);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Config_IsPlainGet_NoRequestBodyNoETagNegotiation()
    {
        // The contract is a plain GET: no request body, no
        // ETag / If-None-Match header expected or required. The
        // server is not required to emit an ETag either (issue
        // Non-Goals). This test exercises the lightweight shape;
        // together with the configured-projection test, it locks in
        // the "GET, no version negotiation" property at the wire
        // level.
        await using var harness = await ConfigHarness.CreateAsync(new CleanupPolicyOptions { RetentionDays = 7 });
        var runnerId = await harness.RegisterRunnerAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runner/{runnerId}/config");
        // Intentionally no If-None-Match and no body.
        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The server is not required to emit an ETag. We assert the
        // absence to lock the "no version negotiation" decision into
        // the test surface.
        Assert.False(response.Headers.Contains("ETag"),
            "/config must not emit an ETag — version negotiation is out of scope (issue #359 Non-Goals)");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, body.GetProperty("cleanupPolicy").GetProperty("retentionDays").GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Config_ResponseShape_IsIdenticalToCleanupPolicyDto()
    {
        // The runner's existing CleanupPolicy TS type parses the
        // body verbatim. The set of keys in
        // RunnerConfigResponse.cleanupPolicy must be exactly the
        // existing CleanupPolicyDto keys: { retentionDays,
        // storageBudgetBytes, storageTargetWatermarkBytes }, all
        // nullable, no new fields, no renamed fields. The shape
        // diverges from the existing dto only in the wrapper: the
        // existing dto was always inlined into WorkDispatchResponse;
        // /config returns a { cleanupPolicy: ... } envelope.
        await using var harness = await ConfigHarness.CreateAsync(new CleanupPolicyOptions
        {
            RetentionDays = 5,
            StorageBudgetBytes = 1024,
            StorageTargetWatermarkBytes = 512,
        });
        var runnerId = await harness.RegisterRunnerAsync();

        using var response = await harness.Client.GetAsync($"/api/runner/{runnerId}/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cleanup = body.GetProperty("cleanupPolicy");
        var keys = cleanup.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "retentionDays", "storageBudgetBytes", "storageTargetWatermarkBytes" }, keys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Poll_IsUnchangedByConfigEndpoint_StillReturns204WhenIdle()
    {
        // /poll's 204-when-idle behavior is untouched by this task
        // (removal is T-002). This test is the regression guard: a
        // future refactor that ties the new /config channel into
        // /poll (e.g. accidentally returning 200 on idle) breaks the
        // contract #359 explicitly calls out as the anti-pattern to
        // fix.
        var policy = new CleanupPolicyOptions { RetentionDays = 9 };
        await using var harness = await ConfigHarness.CreateAsync(policy);
        var runnerId = await harness.RegisterRunnerAsync();

        // Idle path: /poll returns 204 with no body.
        using (var poll = await harness.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, poll.StatusCode);
        }

        // /config is independent and still returns 200.
        using (var config = await harness.Client.GetAsync($"/api/runner/{runnerId}/config"))
        {
            Assert.Equal(HttpStatusCode.OK, config.StatusCode);
            var body = await config.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(9, body.GetProperty("cleanupPolicy").GetProperty("retentionDays").GetInt32());
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Poll_DispatchBody_NoLongerContainsCleanupPolicy()
    {
        // Issue-359 T-002 wire-contract change: cleanupPolicy is
        // removed from WorkDispatchResponse outright on both ends
        // (no compatibility shim). When /poll returns a dispatchable
        // work envelope, the body must NOT contain a cleanupPolicy
        // property — that field's home is now /config exclusively.
        // Seed dispatchable work through the public agent-job validation
        // route, then observe the real HTTP /poll dispatch body. This is
        // the meaningful wire guard: an idle 204 cannot prove the shape of
        // WorkDispatchResponse.
        var policy = new CleanupPolicyOptions { RetentionDays = 9 };
        await using var harness = await ConfigHarness.CreateAsync(policy);
        var projectId = $"runner-config-poll-project-{Guid.NewGuid():N}";
        var runnerId = await harness.RegisterRunnerAsync(projectId, maxWorkflowSlots: 1);
        var jobKey = $"agent-job-runner-config-poll-{Guid.NewGuid():N}";

        var validation = harness.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new
            {
                prompt = "poll body should omit cleanup policy",
                model = "openai/gpt-test",
                jobId = jobKey,
                workspace = new { path = "/tmp/runner-config-poll", projectId },
            });

        var job = harness.Grains.GetGrain<IAgentJobGrain>(jobKey);
        await WaitForAgentJobStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(8));
        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;

        using var response = await harness.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadFirstDispatchElementAsync()
            ?? throw new InvalidOperationException("Expected a dispatch from /poll");
        Assert.Equal(string.Empty, body.GetProperty("workflowRunId").GetString());
        Assert.Equal(workId, body.GetProperty("workId").GetString());
        Assert.Equal("mohist/acp-agent", body.GetProperty("uses").GetString());
        Assert.Equal("agent-job", body.GetProperty("workType").GetString());
        Assert.Equal("agent", body.GetProperty("stage").GetString());
        Assert.Equal("Agent Job", body.GetProperty("title").GetString());
        Assert.Equal(projectId, body.GetProperty("projectId").GetString());
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, body.GetProperty("ownerKind").GetString());
        Assert.Equal(jobKey, body.GetProperty("agentJobId").GetString());
        Assert.Equal(JsonValueKind.String, body.GetProperty("with").ValueKind);
        Assert.Equal(JsonValueKind.String, body.GetProperty("variables").ValueKind);
        Assert.False(
            body.TryGetProperty("cleanupPolicy", out _),
            "/poll dispatch body must not contain a cleanupPolicy property after issue-359 T-002 — the field's home is /config");

        using var report = await harness.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
        {
            workId,
            status = "completed",
            ownerKind = WorkDispatchOwnerKinds.AgentJob,
            agentJobId = jobKey,
            message = "ok",
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        harness.WakeAgentJobValidationAwaiter();

        using var validationResponse = await validation;
        Assert.Equal(HttpStatusCode.OK, validationResponse.StatusCode);
    }

    private static async Task WaitForAgentJobStatusAsync(
        IAgentJobGrain job,
        AgentJobStatus expected,
        TimeSpan timeout)
        => await TestWait.ForAsync(
            () => job.GetStatusAsync(),
            s => s == expected,
            timeout,
            TimeSpan.FromMilliseconds(25),
            $"Agent job to reach {expected}",
            () => job.CheckTimeoutsAsync());

    /// <summary>
    /// Per-test harness that stands up an independent
    /// <see cref="MohistWebApplicationFactory"/> wired with the
    /// requested <see cref="CleanupPolicyOptions"/> values. Each test
    /// owns its own harness so policy values cannot leak across cases.
    /// </summary>
    private sealed class ConfigHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly string _runnerRoot;
        private readonly string _systemUpdateStatePath;
        private readonly MohistWebApplicationFactory _factory;
        private readonly FakeTimeProvider _timeProvider;
        private readonly List<string> _registeredRunnerIds = [];

        private ConfigHarness(
            SqliteConnection keeper,
            MohistWebApplicationFactory factory,
            string runnerRoot,
            string systemUpdateStatePath,
            FakeTimeProvider timeProvider)
        {
            _keeper = keeper;
            _factory = factory;
            _runnerRoot = runnerRoot;
            _systemUpdateStatePath = systemUpdateStatePath;
            _timeProvider = timeProvider;
        }

        public HttpClient Client => _factory.CreateClient();
        public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();

        public static async Task<ConfigHarness> CreateAsync(CleanupPolicyOptions policy)
        {
            var dbName = $"runner-config-{Guid.NewGuid():N}";
            var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var runnerRoot = Path.Combine(Path.GetTempPath(), $"mohist-runner-config-{Guid.NewGuid():N}");
            Directory.CreateDirectory(runnerRoot);
            var systemUpdateStatePath = Path.Combine(Path.GetTempPath(), $"mohist-sys-config-{Guid.NewGuid():N}.json");
            var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
            var factory = new ConfigWebApplicationFactory(
                connectionString, runnerRoot, systemUpdateStatePath, policy, timeProvider);
            // Force host startup so the silo and routes are live
            // before we hand the client out; this matches
            // MohistIntegrationFixture's pattern.
            _ = factory.CreateClient();
            await factory.EnsureSchemaAsync();
            return new ConfigHarness(keeper, factory, runnerRoot, systemUpdateStatePath, timeProvider);
        }

        public async Task<string> RegisterRunnerAsync(string? projectId = null, int? maxWorkflowSlots = null)
        {
            var runnerId = $"runner-config-{Guid.NewGuid():N}";
            using var response = await Client.PostAsJsonAsync($"/api/runner/{runnerId}/register", new
            {
                capabilities = new[] { "spec/*" },
                hostname = "config-host",
                projectId,
            });
            response.EnsureSuccessStatusCode();
            if (maxWorkflowSlots is not null)
            {
                await Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = maxWorkflowSlots.Value });
            }
            var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
            await TestWait.ForAsync(
                () => runner.GetRuntimeStateAsync(),
                s => s.Status == RunnerStatus.Online,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(25),
                $"Runner '{runnerId}' to reach Online");
            _registeredRunnerIds.Add(runnerId);
            return runnerId;
        }

        public void WakeAgentJobValidationAwaiter() =>
            _timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        public async ValueTask DisposeAsync()
        {
            foreach (var runnerId in _registeredRunnerIds)
            {
                using var _ = await Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
            }
            _factory.Dispose();
            await _keeper.DisposeAsync();
            if (Directory.Exists(_runnerRoot))
                Directory.Delete(_runnerRoot, recursive: true);
            if (File.Exists(_systemUpdateStatePath))
                File.Delete(_systemUpdateStatePath);
        }
    }

    /// <summary>
    /// <see cref="MohistWebApplicationFactory"/> subclass that layers a
    /// per-test <see cref="CleanupPolicyOptions"/> on top of the
    /// production configuration binding. We use
    /// <c>Configure&lt;CleanupPolicyOptions&gt;</c> (a second
    /// configuration source) so the production
    /// <c>Mohist:WorkspaceCleanup</c> section still binds first
    /// (preserving any test-fixture defaults) and our test values
    /// then layer on top, which is the pattern other specs in this
    /// project use.
    /// </summary>
    private sealed class ConfigWebApplicationFactory : MohistWebApplicationFactory
    {
        public ConfigWebApplicationFactory(
            string connectionString,
            string runnerRoot,
            string systemUpdateStatePath,
            CleanupPolicyOptions policy,
            FakeTimeProvider timeProvider)
            : base(connectionString, runnerRoot, systemUpdateStatePath, timeProvider)
        {
            _policy = policy;
        }

        private readonly CleanupPolicyOptions _policy;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.Configure<CleanupPolicyOptions>(opts =>
                {
                    opts.RetentionDays = _policy.RetentionDays;
                    opts.StorageBudgetBytes = _policy.StorageBudgetBytes;
                    opts.StorageTargetWatermarkBytes = _policy.StorageTargetWatermarkBytes;
                });
            });
        }
    }
}
