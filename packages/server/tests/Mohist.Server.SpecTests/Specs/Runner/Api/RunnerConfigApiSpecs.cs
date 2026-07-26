using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Orleans.TestingHost;
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
public class RunnerConfigApiSpecs : IClassFixture<RunnerConfigFixture>, IAsyncLifetime
{
    private readonly RunnerConfigFixture _fixture;

    public RunnerConfigApiSpecs(RunnerConfigFixture fixture)
    {
        _fixture = fixture;
    }

    // Each test registers its own runner; unregistering after the test
    // keeps leftover online runners from stealing a later test's agent-job
    // dispatch. The single shared silo/web host is owned by the fixture
    // and persists across the whole class.
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => new(_fixture.UnregisterRunnersAsync());

    [Fact]
    public async Task Config_ConfiguredPolicy_ProjectsAllFields()
    {
        _fixture.SetPolicy(new CleanupPolicyOptions
        {
            RetentionDays = 30,
            StorageBudgetBytes = 1_073_741_824L,
            StorageTargetWatermarkBytes = 536_870_912L,
        });
        var runnerId = await _fixture.RegisterRunnerAsync();

        using var response = await _fixture.Client.GetAsync($"/api/runner/{runnerId}/config");

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
        _fixture.SetPolicy(new CleanupPolicyOptions
        {
            RetentionDays = 0,
            StorageBudgetBytes = -1,
            StorageTargetWatermarkBytes = null,
        });
        var runnerId = await _fixture.RegisterRunnerAsync();

        using var response = await _fixture.Client.GetAsync($"/api/runner/{runnerId}/config");

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

    [Fact]
    public async Task Config_FullyUnconfiguredPolicy_Returns200WithAllNullFields()
    {
        // Default CleanupPolicyOptions — no fields set. The endpoint
        // must still answer 200 with a body; no body omission, no
        // error. The wrapper object is present (so the runner can
        // distinguish "policy available, no strategy enabled" from
        // "policy unavailable" / 404 / network error), and its three
        // inner fields are all present-null.
        _fixture.SetPolicy(new CleanupPolicyOptions());
        var runnerId = await _fixture.RegisterRunnerAsync();

        using var response = await _fixture.Client.GetAsync($"/api/runner/{runnerId}/config");

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
        _fixture.SetPolicy(new CleanupPolicyOptions { RetentionDays = 7 });
        var runnerId = await _fixture.RegisterRunnerAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/runner/{runnerId}/config");
        // Intentionally no If-None-Match and no body.
        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The server is not required to emit an ETag. We assert the
        // absence to lock the "no version negotiation" decision into
        // the test surface.
        Assert.False(response.Headers.Contains("ETag"),
            "/config must not emit an ETag — version negotiation is out of scope (issue #359 Non-Goals)");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, body.GetProperty("cleanupPolicy").GetProperty("retentionDays").GetInt32());
    }

    [Fact]
    public async Task Poll_IsUnchangedByConfigEndpoint_StillReturns204WhenIdle()
    {
        // /poll's 204-when-idle behavior is untouched by this task
        // (removal is T-002). This test is the regression guard: a
        // future refactor that ties the new /config channel into
        // /poll (e.g. accidentally returning 200 on idle) breaks the
        // contract #359 explicitly calls out as the anti-pattern to
        // fix.
        _fixture.SetPolicy(new CleanupPolicyOptions { RetentionDays = 9 });
        var runnerId = await _fixture.RegisterRunnerAsync();

        // Idle path: /poll returns 204 with no body.
        using (var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, poll.StatusCode);
        }

        // /config is independent and still returns 200.
        using (var config = await _fixture.Client.GetAsync($"/api/runner/{runnerId}/config"))
        {
            Assert.Equal(HttpStatusCode.OK, config.StatusCode);
            var body = await config.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(9, body.GetProperty("cleanupPolicy").GetProperty("retentionDays").GetInt32());
        }
    }

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
        _fixture.SetPolicy(new CleanupPolicyOptions { RetentionDays = 9 });
        var projectId = $"runner-config-poll-project-{Guid.NewGuid():N}";
        var runnerId = await _fixture.RegisterRunnerAsync(projectId, maxWorkflowSlots: 1);
        var jobKey = $"agent-job-runner-config-poll-{Guid.NewGuid():N}";

        var validation = _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new
            {
                prompt = "poll body should omit cleanup policy",
                agentId = "agent-validation",
                model = "openai/gpt-test",
                jobId = jobKey,
                workspace = new { path = "/tmp/runner-config-poll", projectId },
            });

        var workId = await _fixture.WaitForAgentJobDispatchAsync(jobKey, runnerId);

        using var response = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadFirstDispatchElementAsync()
            ?? throw new InvalidOperationException("Expected a dispatch from /poll");
        Assert.Equal(string.Empty, body.GetProperty("workflowRunId").GetString());
        Assert.Equal(workId, body.GetProperty("workId").GetString());
        // AgentJob dispatches no longer carry a `Uses` selector — the
        // runner routes on `ownerKind === "agent-job"`, never on the
        // Workflow Action contract (#410 T-001 D1/D2).
        Assert.False(body.TryGetProperty("uses", out _));
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

        using var report = await _fixture.Client.PostAsJsonAsync($"/api/runner/{runnerId}/report", new
        {
            workId,
            status = "completed",
            ownerKind = WorkDispatchOwnerKinds.AgentJob,
            agentJobId = jobKey,
            message = "ok",
        });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        _fixture.WakeAgentJobValidationAwaiter();

        using var validationResponse = await validation;
        Assert.Equal(HttpStatusCode.OK, validationResponse.StatusCode);
    }

}

/// <summary>
/// Stands up a single <see cref="MohistWebApplicationFactory"/> (one silo
/// + one web host) shared by all 9 tests in
/// <see cref="RunnerConfigApiSpecs"/>. Per-test
/// <see cref="CleanupPolicyOptions"/> values are set via
/// <see cref="SetPolicy"/>, which mutates the same mutable POCO the
/// factory's <c>IConfigureOptions&lt;CleanupPolicyOptions&gt;</c> reads
/// at request time (the endpoint uses
/// <c>IOptionsSnapshot&lt;CleanupPolicyOptions&gt;</c>, which re-binds on
/// every request). Unique silo/gateway ports are allocated via
/// <see cref="TestClusterPortAllocator"/> so the class can run in
/// parallel with other integration collections.
/// </summary>
public class RunnerConfigFixture : IAsyncLifetime
{
    private SqliteConnection _keeper = null!;
    private ConfigWebApplicationFactory _factory = null!;
    private readonly List<string> _registeredRunnerIds = [];
    // Allocates distinct silo/gateway ports so the fixture can run in
    // parallel with other integration collections without fighting over
    // 11111 / 30000.
    private TestClusterPortAllocator? _portAllocator;

    public CleanupPolicyOptions Policy { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public IGrainFactory Grains => _factory.Services.GetRequiredService<IGrainFactory>();
    public FakeTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));

    public async ValueTask InitializeAsync()
    {
        var dbName = $"runner-config-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        await _keeper.OpenAsync();

        _portAllocator = new TestClusterPortAllocator();
        var (siloPort, gatewayPort) = _portAllocator.AllocateConsecutivePortPairs(1);
        _factory = new ConfigWebApplicationFactory(
            connectionString,
            "/mohist-tests/runner-config/runner",
            "/mohist-tests/runner-config/system-update.json",
            Policy,
            TimeProvider,
            siloPort,
            gatewayPort);
        Client = _factory.CreateClient();
        await _factory.EnsureSchemaAsync();
    }

    /// <summary>
    /// Overwrites all three policy fields. Called at the start of each
    /// test so no value leaks from the previous test's configuration.
    /// </summary>
    public void SetPolicy(CleanupPolicyOptions policy)
    {
        Policy.RetentionDays = policy.RetentionDays;
        Policy.StorageBudgetBytes = policy.StorageBudgetBytes;
        Policy.StorageTargetWatermarkBytes = policy.StorageTargetWatermarkBytes;
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

    public async Task<string> WaitForAgentJobDispatchAsync(string agentJobId, string expectedRunnerId)
    {
        var assignment = await _factory.Services
            .GetRequiredService<AgentJobDispatchProbe>()
            .WaitForRunnerAcceptedAsync(agentJobId);
        Assert.Equal(expectedRunnerId, assignment.RunnerId);
        return assignment.WorkId;
    }

    public void WakeAgentJobValidationAwaiter() =>
        TimeProvider.Advance(TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Unregisters every runner created since the last call so leftover
    /// online runners cannot steal a later test's agent-job dispatch.
    /// Called after each test by the spec class's own
    /// <c>IAsyncLifetime.DisposeAsync</c>.
    /// </summary>
    public async Task UnregisterRunnersAsync()
    {
        foreach (var runnerId in _registeredRunnerIds)
        {
            try { using var _ = await Client.PostAsync($"/api/runner/{runnerId}/unregister", null); }
            catch { /* best-effort cleanup between tests */ }
        }
        _registeredRunnerIds.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        _factory?.Dispose();
        await _keeper.DisposeAsync();
        _portAllocator?.Dispose();
    }

    /// <summary>
    /// <see cref="MohistWebApplicationFactory"/> subclass that layers a
    /// per-test <see cref="CleanupPolicyOptions"/> on top of the
    /// production configuration binding. The factory holds a reference to
    /// the mutable <see cref="Policy"/> instance owned by
    /// <see cref="RunnerConfigFixture"/>; the
    /// <c>Configure&lt;CleanupPolicyOptions&gt;</c> callback reads from
    /// that reference at request time (via
    /// <c>IOptionsSnapshot&lt;CleanupPolicyOptions&gt;</c>), so mutating
    /// the POCO between tests changes what the next HTTP request sees
    /// without rebuilding the silo.
    /// </summary>
    private sealed class ConfigWebApplicationFactory : MohistWebApplicationFactory
    {
        private readonly CleanupPolicyOptions _policy;

        public ConfigWebApplicationFactory(
            string connectionString,
            string runnerRoot,
            string systemUpdateStatePath,
            CleanupPolicyOptions policy,
            FakeTimeProvider timeProvider,
            int siloPort,
            int gatewayPort)
            : base(connectionString, runnerRoot, systemUpdateStatePath, timeProvider, siloPort, gatewayPort)
        {
            _policy = policy;
        }

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
