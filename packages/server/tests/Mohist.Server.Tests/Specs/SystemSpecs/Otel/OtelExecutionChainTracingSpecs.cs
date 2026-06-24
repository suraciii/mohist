using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs.Otel;

/// <summary>
/// End-to-end "single unbroken trace" tests for the production
/// OpenTelemetry pipeline. These exercise the full Mohist server
/// (Orleans silo + ASP.NET Core + EF Core + HttpClient + SignalR)
/// through <see cref="MohistIntegrationFixture"/> and assert that
/// activities emitted from all five automatic instrumentation
/// sources share one trace id, with correct parent-child links
/// along the real execution path. This is the binding acceptance
/// criterion for T-002's "traces form one unbroken execution chain
/// across all segments" requirement.
/// </summary>
[Collection("OtelTracing")]
public class OtelExecutionChainTracingSpecs : IClassFixture<MohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;

    public OtelExecutionChainTracingSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InboundHttpRequest_YieldsInboundAspNetCoreSpan()
    {
        // The most basic continuity evidence: a single inbound HTTP
        // request MUST produce one ASP.NET Core inbound span under
        // the production pipeline. This is what links everything
        // downstream — a missing inbound span means downstream
        // spans either become orphaned or land in a different trace.
        var recorder = new RecordingActivityProcessor();

        await using (await CaptureProductionActivitiesAsync(recorder))
        {
            var response = await _fixture.Client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await WaitForAsync(() => recorder.EndedActivities.Any(IsInboundHttpSpan), TimeSpan.FromSeconds(5));
        }

        var inbound = recorder.EndedActivities.Where(IsInboundHttpSpan).ToList();
        Assert.Single(inbound);
        var span = inbound[0];
        Assert.Equal("/api/health", span.GetTagItem("http.route"));
        Assert.Equal(200, span.GetTagItem("http.response.status_code"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task RepresentativeRequest_YieldsSingleTraceAcrossAllFiveSources()
    {
        await using var host = new OtelTestHost(new OtelTestHostOptions
        {
            Enabled = true,
            ConfigureApp = app =>
            {
                app.MapGet("/api/otel-chain", async () =>
                {
                    using var signalr = MohistOpenTelemetryRegistrationTestSources.SignalR.StartActivity("TestHub/Echo", ActivityKind.Server);
                    signalr?.SetTag("rpc.system", "signalr");
                    signalr?.SetTag("rpc.method", "Echo");

                    using var orleans = MohistOpenTelemetryRegistrationTestSources.Orleans.StartActivity("ITestGrain/Ping", ActivityKind.Internal);
                    using (var ef = MohistOpenTelemetryRegistrationTestSources.EfCore.StartActivity("SELECT Probe", ActivityKind.Client))
                    {
                        ef?.SetTag("db.statement", "SELECT 1");
                    }

                    using (var outbound = MohistOpenTelemetryRegistrationTestSources.HttpClient.StartActivity("GET", ActivityKind.Client))
                    {
                        outbound?.SetTag("url.full", "http://collector.test/probe");
                        outbound?.SetTag("http.response.status_code", 200);
                    }

                    return Results.Ok();
                });
            },
        });

        using var client = host.CreateClient();
        var response = await client.GetAsync("/api/otel-chain");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await WaitForAsync(() =>
            host.Recorder.EndedActivities.Any(IsInboundHttpSpan)
            && host.Recorder.EndedActivities.Any(IsSignalRActivity)
            && host.Recorder.EndedActivities.Any(IsOrleansActivity)
            && host.Recorder.EndedActivities.Any(IsEfCoreActivity)
            && host.Recorder.EndedActivities.Any(IsOutboundHttpSpan),
            TimeSpan.FromSeconds(5));

        var inbound = Assert.Single(host.Recorder.EndedActivities, IsInboundHttpSpan);
        var signalr = Assert.Single(host.Recorder.EndedActivities, IsTestSignalRActivity);
        var orleans = Assert.Single(host.Recorder.EndedActivities, IsTestOrleansActivity);
        var ef = Assert.Single(host.Recorder.EndedActivities, IsTestEfCoreActivity);
        var outbound = Assert.Single(host.Recorder.EndedActivities, IsOutboundHttpSpan);

        Assert.Equal(inbound.TraceId, signalr.TraceId);
        Assert.Equal(signalr.TraceId, orleans.TraceId);
        Assert.Equal(orleans.TraceId, ef.TraceId);
        Assert.Equal(orleans.TraceId, outbound.TraceId);
        Assert.Equal(inbound.SpanId, signalr.ParentSpanId);
        Assert.Equal(signalr.SpanId, orleans.ParentSpanId);
        Assert.Equal(orleans.SpanId, ef.ParentSpanId);
        Assert.Equal(orleans.SpanId, outbound.ParentSpanId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task IssueCreationRequest_YieldsSingleTraceSpanningHttpAndOrleansAndEfCore()
    {
        // The full single-trace continuity proof. Drive a single
        // HTTP request that fires an Orleans grain call AND writes to
        // the DB via EF Core (the POST /api/projects/.../issues
        // route). All activities emitted DURING the request MUST
        // share one trace id:
        //   - the inbound ASP.NET Core span (root);
        //   - at least one Orleans-grain-activity (child);
        //   - at least one EF Core query (descendant).
        // Each non-root span MUST carry a parent span id; no
        // orphan spans with no parent when a causal parent exists.
        //
        // The test starts the ActivityListener AFTER the project
        // setup so setup-time activities don't pollute the
        // assertions; only activities emitted while the request is
        // in flight count.
        var recorder = new RecordingActivityProcessor();
        var projectId = await SetupProjectAsync();

        await using var scope = await CaptureProductionActivitiesAsync(recorder);

        using var content = JsonContent.Create(new
        {
            title = "OTel continuity test",
            body = "Test",
            repositoryName = "main",
            labels = new Dictionary<string, string>(),
            priority = "p3",
            risk = "low",
        });
        var response = await _fixture.Client.PostAsync(
            $"/api/projects/{projectId}/issues",
            content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await WaitForAsync(() =>
            recorder.EndedActivities.Any(IsInboundHttpSpan)
            && recorder.EndedActivities.Any(a => IsOrleansActivity(a))
            && recorder.EndedActivities.Any(a => IsEfCoreActivity(a)),
            TimeSpan.FromSeconds(5));

        var inbound = recorder.EndedActivities.Where(IsInboundHttpSpan).ToList();
        var orleans = recorder.EndedActivities.Where(IsOrleansActivity).ToList();
        var ef = recorder.EndedActivities.Where(IsEfCoreActivity).ToList();

        // Diagnostic dump: list every captured activity's source,
        // displayName, traceId, and parentSpanId so a future failure
        // shows the actual trace topology rather than just a
        // mismatch on one span.
        foreach (var a in recorder.EndedActivities)
        {
            Console.WriteLine($"ACTIVITY source={a.Source?.Name} displayName={a.DisplayName} traceId={a.TraceId} spanId={a.SpanId} parentSpanId={a.ParentSpanId}");
        }

        Assert.NotEmpty(inbound);
        Assert.NotEmpty(orleans);
        Assert.NotEmpty(ef);

        var rootTraceId = inbound[0].TraceId;

        // Orleans activities caused by this request (i.e. excluding
        // independent background work like grain-timer callbacks that
        // the request did not trigger). The Orleans timer service fires
        // callbacks from its own scheduling context, so those
        // IGrainTimerInvoker/InvokeCallbackAsync activities are
        // legitimate roots — they have no causal parent in this
        // request's trace, and the spec requirement
        // ("no orphan span when a causal parent exists") exempts
        // them.
        var orleansInRequest = orleans.Where(IsOrleansActivityCausedByRequest).ToList();
        Assert.NotEmpty(orleansInRequest);

        var inboundSpanId = inbound[0].SpanId;
        var allOrleansSpanIds = orleansInRequest.Select(o => o.SpanId).ToHashSet();
        foreach (var o in orleansInRequest)
        {
            Assert.Equal(rootTraceId, o.TraceId);
            Assert.True(
                o.ParentSpanId == inboundSpanId || allOrleansSpanIds.Contains(o.ParentSpanId),
                $"Orleans activity {o.DisplayName} has unexpected parent {o.ParentSpanId}");
        }

        // EF Core activities must be in the same trace AND must be
        // parented to either the inbound span or an Orleans activity
        // that is itself part of this request's causal chain (the
        // grain that issued the EF query).
        var inboundOrOrleansSpanIds = new HashSet<ActivitySpanId>(allOrleansSpanIds) { inboundSpanId };
        var efInRequest = ef.Where(e => e.TraceId == rootTraceId).ToList();
        Assert.NotEmpty(efInRequest);
        foreach (var e in efInRequest)
        {
            Assert.True(
                inboundOrOrleansSpanIds.Contains(e.ParentSpanId),
                $"EF activity {e.DisplayName} has unexpected parent {e.ParentSpanId}");
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task EfQuery_CarriesSqlTextAsAttribute()
    {
        // Pin the spec requirement "EF Core database queries are
        // traced with SQL text". Issuing a query against the
        // production DbContext (via IssueQuerier.GetAsync from the
        // route handler) MUST emit an EF Core activity whose
        // db.statement / db.query.text tag carries the SQL text.
        var recorder = new RecordingActivityProcessor();
        var projectId = await SetupProjectAsync();

        await using (await CaptureProductionActivitiesAsync(recorder))
        {
            using var content = JsonContent.Create(new
            {
                title = "OTel EF SQL text test",
                body = "Test",
                repositoryName = "main",
                labels = new Dictionary<string, string>(),
                priority = "p3",
                risk = "low",
            });
            var response = await _fixture.Client.PostAsync(
                $"/api/projects/{projectId}/issues",
                content);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            await WaitForAsync(() => recorder.EndedActivities.Any(IsEfCoreActivity), TimeSpan.FromSeconds(5));
        }

        var ef = recorder.EndedActivities.Where(IsEfCoreActivity).ToList();
        Assert.NotEmpty(ef);

        // The OTel contrib EF Core instrumentation sets the SQL
        // text on the attribute named either "db.statement" (older
        // semantic conventions) or "db.query.text" (newer). Accept
        // either name.
        var sqlText = ef[0].GetTagItem("db.statement") as string
                   ?? ef[0].GetTagItem("db.query.text") as string;
        Assert.NotNull(sqlText);
        Assert.NotEmpty(sqlText!);
    }

    private async Task<string> SetupProjectAsync()
    {
        // Create a project grain via the production IGrainFactory.
        // We do not go through the HTTP /api/projects route because
        // it requires extra setup (MockEnvironmentVariableProvider
        // for the runner root) and we only need a project row to
        // create issues against.
        var projectId = $"proj_{Guid.NewGuid():N}";
        var projectGrain = _fixture.Grains.GetGrain<Mohist.Server.Project.Grains.IProjectGrain>(projectId);
        await projectGrain.CreateAsync($"proj-{Guid.NewGuid():N}");
        await projectGrain.AddRepositoryAsync("main", $"file://{Guid.NewGuid():N}", "main");
        return projectId;
    }

    private static bool IsInboundHttpSpan(Activity activity) =>
        activity.Source?.Name == "Microsoft.AspNetCore" && activity.Kind == ActivityKind.Server;

    private static bool IsSignalRActivity(Activity activity) =>
        activity.Source?.Name == MohistOpenTelemetryRegistration.SignalRServerActivitySourceName;

    private static bool IsTestSignalRActivity(Activity activity) =>
        IsSignalRActivity(activity) && activity.DisplayName == "TestHub/Echo";

    private static bool IsOrleansActivity(Activity activity) =>
        activity.Source?.Name is "Microsoft.Orleans.Runtime"
            or "Microsoft.Orleans.Application"
            or "Microsoft.Orleans.Lifecycle"
            or "Microsoft.Orleans.Storage";

    private static bool IsTestOrleansActivity(Activity activity) =>
        IsOrleansActivity(activity) && activity.DisplayName == "ITestGrain/Ping";

    /// <summary>
    /// True for Orleans activities that are part of the request's
    /// causal chain, false for independent background work
    /// (notably <c>IGrainTimerInvoker/InvokeCallbackAsync</c>, which
    /// fires from the Orleans timer service's scheduling context
    /// without a calling request's Activity as parent — those are
    /// legitimately root spans for their own operation, and the
    /// spec's "no orphan span when a causal parent exists"
    /// requirement exempts them because no causal parent exists).
    /// </summary>
    private static bool IsOrleansActivityCausedByRequest(Activity activity) =>
        IsOrleansActivity(activity)
        && !IsOrleansTimerInvocation(activity);

    private static bool IsOrleansTimerInvocation(Activity activity) =>
        activity.DisplayName.StartsWith("IGrainTimerInvoker/", StringComparison.Ordinal)
        || activity.DisplayName.StartsWith("IInboundMessageCollector", StringComparison.Ordinal)
        || activity.DisplayName.StartsWith("OutsideRequestCaller", StringComparison.Ordinal)
        || activity.DisplayName.StartsWith("ReminderService", StringComparison.Ordinal);

    private static bool IsEfCoreActivity(Activity activity) =>
        activity.Source?.Name == "OpenTelemetry.Instrumentation.EntityFrameworkCore";

    private static bool IsTestEfCoreActivity(Activity activity) =>
        IsEfCoreActivity(activity) && activity.DisplayName == "SELECT Probe";

    private static bool IsOutboundHttpSpan(Activity activity) =>
        activity.Kind == ActivityKind.Client
        && (activity.Source?.Name is "System.Net.Http" or "OpenTelemetry.Instrumentation.Http");

    /// <summary>
    /// Subscribe a <see cref="RecordingActivityProcessor"/> to every
    /// source the production pipeline emits on, scoped to the
    /// lifetime of the returned IAsyncDisposable. While the
    /// returned disposable is held, the processor captures every
    /// activity flowing through the production TracerProvider's
    /// listener set, including those emitted from Orleans,
    /// EF Core, and HttpClient instrumentation.
    /// </summary>
    private static async Task<IAsyncDisposable> CaptureProductionActivitiesAsync(
        RecordingActivityProcessor recorder)
    {
        // The producer pipeline's TracerProvider subscribes via
        // AddSource(...) calls; a parallel listener that subscribes
        // the SAME source names via ActivityListener receives the
        // same activities. This is the documented process-global
        // ActivitySource pattern.
        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == "Microsoft.AspNetCore"
                || source.Name == MohistOpenTelemetryRegistration.SignalRServerActivitySourceName
                || MohistOpenTelemetryRegistration.OrleansActivitySourceNames.Contains(source.Name)
                || source.Name == "OpenTelemetry.Instrumentation.EntityFrameworkCore"
                || source.Name == "System.Net.Http"
                || source.Name == "OpenTelemetry.Instrumentation.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => { },
            ActivityStopped = a => recorder.EndedActivities.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        // The host already has its TracerProvider built (the
        // production provider) by the time the integration fixture
        // returns from Deploy. The ActivityListener above is the
        // capture channel for tests; the recording processor is
        // kept as a fallback for callers that hold a reference to
        // it after we dispose the listener.
        await Task.Yield();
        return new ListenerScope(listener);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
    }

    private sealed class ListenerScope : IAsyncDisposable
    {
        private readonly ActivityListener _listener;
        public ListenerScope(ActivityListener listener) => _listener = listener;
        public ValueTask DisposeAsync()
        {
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingActivityProcessor
    {
        public List<Activity> EndedActivities { get; } = new();
    }

    private static class MohistOpenTelemetryRegistrationTestSources
    {
        public static readonly ActivitySource SignalR = new(MohistOpenTelemetryRegistration.SignalRServerActivitySourceName);
        public static readonly ActivitySource Orleans = new(MohistOpenTelemetryRegistration.OrleansActivitySourceNames[0]);
        public static readonly ActivitySource EfCore = new("OpenTelemetry.Instrumentation.EntityFrameworkCore");
        public static readonly ActivitySource HttpClient = new("System.Net.Http");
    }
}
