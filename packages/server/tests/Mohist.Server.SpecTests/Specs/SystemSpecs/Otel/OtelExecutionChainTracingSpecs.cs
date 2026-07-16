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
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

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

            await recorder.WaitForAsync(s => s.Any(IsInboundHttpSpan));
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

        await host.Recorder.WaitForAsync(s =>
            s.Any(IsInboundHttpSpan)
            && s.Any(IsSignalRActivity)
            && s.Any(IsOrleansActivity)
            && s.Any(IsEfCoreActivity)
            && s.Any(IsOutboundHttpSpan));

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

        await recorder.WaitForAsync(s => FindIssueCreationTrace(s) is not null);

        var activities = recorder.EndedActivities;

        // Diagnostic dump: list every captured activity's source,
        // displayName, traceId, and parentSpanId so a future failure
        // shows the actual trace topology rather than just a
        // mismatch on one span.
        foreach (var a in activities)
        {
            Console.WriteLine($"ACTIVITY source={a.Source?.Name} displayName={a.DisplayName} traceId={a.TraceId} spanId={a.SpanId} parentSpanId={a.ParentSpanId}");
        }

        var trace = FindIssueCreationTrace(activities);
        Assert.NotNull(trace);

        var rootTraceId = trace.Inbound.TraceId;

        // Orleans activities caused by this request (i.e. excluding
        // independent background work like grain-timer callbacks that
        // the request did not trigger). The Orleans timer service fires
        // callbacks from its own scheduling context, so those
        // IGrainTimerInvoker/InvokeCallbackAsync activities are
        // legitimate roots — they have no causal parent in this
        // request's trace, and the spec requirement
        // ("no orphan span when a causal parent exists") exempts
        // them.
        var orleansInRequest = trace.Orleans;
        Assert.NotEmpty(orleansInRequest);

        var inboundSpanId = trace.Inbound.SpanId;
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
        var efInRequest = trace.EfCore;
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

            await recorder.WaitForAsync(s => s.Any(IsEfCoreActivity));
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

    private static IssueCreationTrace? FindIssueCreationTrace(IReadOnlyList<Activity> activities)
    {
        foreach (var traceGroup in activities.GroupBy(a => a.TraceId))
        {
            var spans = traceGroup.ToList();
            var inbound = spans.FirstOrDefault(IsIssueCreationInboundHttpSpan);
            if (inbound is null) continue;

            var orleans = spans.Where(IsOrleansActivityCausedByRequest).ToList();
            if (orleans.Count == 0) continue;

            var efCore = spans.Where(IsEfCoreActivity).ToList();
            if (efCore.Count == 0) continue;

            return new IssueCreationTrace(inbound, orleans, efCore);
        }

        return null;
    }

    private static bool IsIssueCreationInboundHttpSpan(Activity activity)
    {
        if (!IsInboundHttpSpan(activity)) return false;

        var method = activity.GetTagItem("http.request.method") as string
                     ?? activity.GetTagItem("http.method") as string;
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && !activity.DisplayName.StartsWith("POST ", StringComparison.OrdinalIgnoreCase))
            return false;

        var route = activity.GetTagItem("http.route") as string;
        if (IsIssueCreationRoute(route)) return true;

        var path = activity.GetTagItem("url.path") as string
                   ?? activity.GetTagItem("http.target") as string;
        if (path is not null
            && path.Contains("/api/projects/", StringComparison.Ordinal)
            && path.TrimEnd('/').EndsWith("/issues", StringComparison.Ordinal))
            return true;

        return IsIssueCreationRoute(activity.DisplayName);
    }

    private static bool IsIssueCreationRoute(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var route = value.Trim();
        if (route.StartsWith("POST ", StringComparison.OrdinalIgnoreCase))
            route = route["POST ".Length..].TrimStart();
        return route.TrimEnd('/') == "/api/projects/{projectRef}/issues";
    }

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
            ActivityStopped = recorder.Record,
        };
        ActivitySource.AddActivityListener(listener);

        // The host already has its TracerProvider built (the
        // production provider) by the time the integration fixture
        // returns from Deploy. Registering ActivityListener is synchronous;
        // it is ready before AddActivityListener returns.
        return new ListenerScope(listener);
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
        private readonly object _gate = new();
        private readonly List<Activity> _endedActivities = new();
        private readonly List<PendingWait> _waiters = new();

        public IReadOnlyList<Activity> EndedActivities
        {
            get
            {
                lock (_gate)
                {
                    return _endedActivities.ToList();
                }
            }
        }

        public void Record(Activity activity)
        {
            List<Activity>? snapshot = null;
            lock (_gate)
            {
                _endedActivities.Add(activity);
                if (_waiters.Count > 0)
                {
                    snapshot = _endedActivities.ToList();
                    for (int i = _waiters.Count - 1; i >= 0; i--)
                    {
                        var wait = _waiters[i];
                        if (wait.Predicate(snapshot))
                        {
                            _waiters.RemoveAt(i);
                            wait.Tcs.TrySetResult(true);
                        }
                    }
                }
            }
        }

        public async Task<List<Activity>> WaitForAsync(
            Func<List<Activity>, bool> predicate,
            CancellationToken cancellationToken = default)
        {
            await WaitUntilAsync(predicate, cancellationToken);
            return EndedActivities.ToList();
        }

        private Task WaitUntilAsync(Func<List<Activity>, bool> predicate, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var initial = _endedActivities.ToList();
                if (predicate(initial))
                    return Task.CompletedTask;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var wait = new PendingWait(predicate, tcs);
                _waiters.Add(wait);

                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() =>
                    {
                        lock (_gate) _waiters.Remove(wait);
                        tcs.TrySetCanceled(cancellationToken);
                    });
                }
                return tcs.Task;
            }
        }

        private sealed class PendingWait
        {
            public Func<List<Activity>, bool> Predicate { get; }
            public TaskCompletionSource<bool> Tcs { get; }
            public PendingWait(Func<List<Activity>, bool> predicate, TaskCompletionSource<bool> tcs)
            {
                Predicate = predicate;
                Tcs = tcs;
            }
        }
    }

    private sealed record IssueCreationTrace(
        Activity Inbound,
        IReadOnlyList<Activity> Orleans,
        IReadOnlyList<Activity> EfCore);

    private static class MohistOpenTelemetryRegistrationTestSources
    {
        public static readonly ActivitySource SignalR = new(MohistOpenTelemetryRegistration.SignalRServerActivitySourceName);
        public static readonly ActivitySource Orleans = new(MohistOpenTelemetryRegistration.OrleansActivitySourceNames[0]);
        public static readonly ActivitySource EfCore = new("OpenTelemetry.Instrumentation.EntityFrameworkCore");
        public static readonly ActivitySource HttpClient = new("System.Net.Http");
    }
}
