using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.HostLifecycle;

public class MohistHostPlanCompositionSpecs
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static WebApplicationBuilder NewBuilder() =>
        WebApplication.CreateBuilder(Array.Empty<string>());

    [Fact]
    public void PrimaryAndAlternatePlans_ShareEpochAndEnabledAndDifferOnlyInListenerAndCollectorSeed()
    {
        var epoch = new RuntimeEpoch(Start);
        var primary = MohistHostPlan.Primary(
            epoch,
            enabled: true,
            listenerIntent: new OtelListenerIntent("localhost", 4318));
        var alternate = MohistHostPlan.Alternate(primary);

        Assert.Same(epoch, alternate.Epoch);
        Assert.Equal(primary.Enabled, alternate.Enabled);
        Assert.NotNull(primary.ListenerIntent);
        Assert.Null(alternate.ListenerIntent);
        Assert.False(alternate.InitialCollectorResult.IsOnline);
        Assert.Equal(
            RuntimeDegradationCodes.CollectorBindFailed,
            alternate.InitialCollectorResult.FailureCode);
    }

    [Fact]
    public void ApplyPlan_RegistersExactlyOneDiagnosticsSamplerPerPlan()
    {
        var primaryPlan = MohistHostPlan.Primary(
            new RuntimeEpoch(Start),
            enabled: true,
            listenerIntent: new OtelListenerIntent("localhost", 4318));
        var alternatePlan = MohistHostPlan.Alternate(primaryPlan);

        var primaryBuilder = NewBuilder();
        MohistHostFactory.ApplyPlan(primaryPlan, primaryBuilder);
        Assert.Single(
            primaryBuilder.Services,
            d => d.ImplementationType?.FullName?.Contains("OtelDiagnosticsSampler", StringComparison.Ordinal) == true);

        var alternateBuilder = NewBuilder();
        MohistHostFactory.ApplyPlan(alternatePlan, alternateBuilder);
        Assert.Single(
            alternateBuilder.Services,
            d => d.ImplementationType?.FullName?.Contains("OtelDiagnosticsSampler", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ApplyPlan_PreservesEnabledIntentAcrossPrimaryAndAlternate()
    {
        var epoch = new RuntimeEpoch(Start);
        var enabledPlan = MohistHostPlan.Primary(epoch, true, new OtelListenerIntent("localhost", 4318));
        var disabledPlan = MohistHostPlan.Primary(epoch, false, listenerIntent: null);

        Assert.True(enabledPlan.Enabled);
        Assert.True(MohistHostPlan.Alternate(enabledPlan).Enabled);
        Assert.False(disabledPlan.Enabled);
        Assert.False(MohistHostPlan.Alternate(disabledPlan).Enabled);
    }

    [Fact]
    public void ApplyPlan_RegistersSharedApiAndRuntimeMarkers()
    {
        var primaryPlan = MohistHostPlan.Primary(
            new RuntimeEpoch(Start),
            enabled: true,
            listenerIntent: new OtelListenerIntent("localhost", 4318));
        var alternatePlan = MohistHostPlan.Alternate(primaryPlan);

        var primaryBuilder = NewBuilder();
        MohistHostFactory.ApplyPlan(primaryPlan, primaryBuilder);
        AssertPrimarySingletons(primaryBuilder.Services);

        var alternateBuilder = NewBuilder();
        MohistHostFactory.ApplyPlan(alternatePlan, alternateBuilder);
        AssertPrimarySingletons(alternateBuilder.Services);
    }

    private static void AssertPrimarySingletons(IServiceCollection services)
    {
        var otelDbDescriptors = services
            .Where(d => d.ImplementationType?.FullName == "Mohist.Server.Otel.OtelDb")
            .ToArray();
        Assert.Single(otelDbDescriptors);

        Assert.Contains(services, d => d.ServiceType == typeof(RuntimeEpoch));
        Assert.Contains(services, d => d.ServiceType == typeof(RuntimeObservability));

        var samplerDescriptors = services
            .Where(d => d.ImplementationType?.FullName?.Contains("OtelDiagnosticsSampler", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Single(samplerDescriptors);
    }
}
