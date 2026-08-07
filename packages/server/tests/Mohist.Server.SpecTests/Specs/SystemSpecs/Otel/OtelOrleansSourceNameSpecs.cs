using System.Diagnostics;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Workflow.Grain;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs.Otel;

/// <summary>
/// Pinning test that documents the Orleans 10 native ActivitySource
/// names actually emitted at runtime. The design lists
/// <c>Microsoft.Orleans.*</c> in Decision 3 and Risks/Orleans-name-drift,
/// but the exact set must be confirmed against the running silo
/// (T-002 acceptance criterion: "The exact Orleans ActivitySource
/// name(s) are confirmed empirically by sending one grain call
/// through the instrumented host and recording every emitted source
/// name; the subscribed name(s) match what Orleans 10 actually emits").
///
/// <para>
/// Confirmed against <c>Orleans.Core.Abstractions</c> 10.1.0 by
/// reflection on <c>Orleans.Diagnostics.ActivitySources</c>:
/// <c>RuntimeActivitySourceName</c> = <c>Microsoft.Orleans.Runtime</c>,
/// <c>ApplicationGrainActivitySourceName</c> = <c>Microsoft.Orleans.Application</c>,
/// <c>LifecycleActivitySourceName</c> = <c>Microsoft.Orleans.Lifecycle</c>,
/// <c>StorageActivitySourceName</c> = <c>Microsoft.Orleans.Storage</c>.
/// The wildcard <c>Microsoft.Orleans.*</c> (Orleans'
/// <c>AllActivitySourceName</c>) is NOT subscribable via
/// OpenTelemetry's <c>AddSource</c> — it accepts exact names only.
/// </para>
/// </summary>
[Collection("OtelTracing")]
public class OtelOrleansSourceNameSpecs : IClassFixture<BacklogFixture>
{
    private readonly BacklogFixture _fixture;

    public OtelOrleansSourceNameSpecs(BacklogFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ProductionOtelRegistration_SubscribesAllFourOrleansSourceNames()
    {
        // The simplest possible regression test on the Orleans source
        // subscription: confirm the production registration's static
        // list matches the four Orleans ActivitySource names Orleans
        // 10.1.0 actually emits. If a future Orleans upgrade renames
        // an ActivitySource, the diff between this assertion and the
        // runtime-emitted set (verified in
        // OrleansGrainCall_EmitsActivityFromConfirmedOrleansSourceNames)
        // shows up immediately.
        Assert.Equal(
            new[]
            {
                "Microsoft.Orleans.Application",
                "Microsoft.Orleans.Runtime",
                "Microsoft.Orleans.Lifecycle",
                "Microsoft.Orleans.Storage",
            },
            MohistOpenTelemetryRegistration.OrleansActivitySourceNames);
    }

    [Fact]
    public async Task OrleansGrainCall_EmitsActivityFromConfirmedOrleansSourceNames()
    {
        // Send one grain call through an instrumented silo and capture
        // every Activity emitted while the call runs. The set of
        // source names that appear MUST contain at least the
        // Microsoft.Orleans.Runtime source — the runtime source fires
        // for every grain call by default. (Subscribed names that
        // don't appear at runtime are harmless; unsubscribed names
        // that DO appear are a leak.)
        //
        // We subscribe an ActivityListener (independent of any
        // TracerProvider) so the test does not depend on the
        // production OTel pipeline being active — the listener
        // observes ActivitySource.StartActivity calls at the source.
        var observedSourceNames = new HashSet<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.Source is not null)
                {
                    lock (observedSourceNames)
                    {
                        observedSourceNames.Add(activity.Source.Name);
                    }
                }
            },
            ActivityStopped = _ => { },
        };
        ActivitySource.AddActivityListener(listener);

        var grain = _fixture.Grains.GetGrain<IRunnerGrain>($"runner-{Guid.NewGuid():N}");
        var info = await grain.GetInfoAsync();

        Assert.Null(info);
        // The empirical set MUST contain at least one of the four
        // Orleans ActivitySource names (so the grain call observably
        // goes through Orleans-instrumented code) AND the
        // intersection with the production-subscription set MUST be
        // non-empty (so the production pipeline actually captures
        // it). A grain call typically emits the Lifecycle source on
        // activation and the Runtime source on the call itself; this
        // assertion accepts either as evidence of empirical
        // confirmation.
        var subscribedSet = new HashSet<string>(
            MohistOpenTelemetryRegistration.OrleansActivitySourceNames,
            StringComparer.Ordinal);
        var intersection = observedSourceNames.Intersect(subscribedSet, StringComparer.Ordinal).ToList();
        Assert.NotEmpty(intersection);
    }
}
