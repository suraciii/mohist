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
    public void CreatePrimaryPlan_ReadsPreparedBuilderConfiguration()
    {
        var builder = NewBuilder();
        builder.Environment.EnvironmentName = MohistHostEnvironment.Testing;
        builder.Configuration["Mohist:Otel:Enabled"] = "true";
        builder.Configuration["Mohist:Otel:BindHost"] = "127.0.0.1";
        builder.Configuration["Mohist:Otel:Port"] = "54321";
        var factory = new MohistHostFactory([], builder);

        var plan = factory.CreatePrimaryPlan(new RuntimeEpoch(Start));

        Assert.True(plan.Enabled);
        Assert.Equal(new OtelListenerIntent("127.0.0.1", 54321), plan.ListenerIntent);
    }

    [Fact]
    public void CreatePrimaryPlan_MissingEnablementUsesProtectedLocalDefaults()
    {
        var builder = NewBuilder();
        builder.Environment.EnvironmentName = MohistHostEnvironment.Testing;
        var factory = new MohistHostFactory([], builder);

        var plan = factory.CreatePrimaryPlan(new RuntimeEpoch(Start));

        Assert.True(plan.Enabled);
        Assert.Equal(new OtelListenerIntent("localhost", 4318), plan.ListenerIntent);
    }

    [Fact]
    public void CreatePrimaryPlan_ExplicitFalseOmitsCollectorListener()
    {
        var builder = NewBuilder();
        builder.Environment.EnvironmentName = MohistHostEnvironment.Testing;
        builder.Configuration["Mohist:Otel:Enabled"] = "false";
        var factory = new MohistHostFactory([], builder);

        var plan = factory.CreatePrimaryPlan(new RuntimeEpoch(Start));

        Assert.False(plan.Enabled);
        Assert.Null(plan.ListenerIntent);
    }

    [Fact]
    public void CreatePrimaryPlan_ZeroCollectorPortKeepsOtelEnabledWithoutBindingAListener()
    {
        var builder = NewBuilder();
        builder.Environment.EnvironmentName = MohistHostEnvironment.Testing;
        builder.Configuration["Mohist:Otel:Enabled"] = "true";
        builder.Configuration["Mohist:Otel:Port"] = "0";
        var factory = new MohistHostFactory([], builder);

        var plan = factory.CreatePrimaryPlan(new RuntimeEpoch(Start));

        Assert.True(plan.Enabled);
        Assert.Null(plan.ListenerIntent);
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
    public void ApplyPlan_DisabledOtelDoesNotRegisterDiagnosticsSampler()
    {
        var builder = NewBuilder();
        builder.Configuration["Mohist:Otel:Enabled"] = "false";
        var plan = MohistHostPlan.Primary(
            new RuntimeEpoch(Start),
            enabled: false,
            listenerIntent: null);

        MohistHostFactory.ApplyPlan(plan, builder);

        Assert.DoesNotContain(
            builder.Services,
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
