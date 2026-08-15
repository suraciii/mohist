using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Logging;
using Mohist.Server.Otel;

namespace Mohist.Server.Infrastructure.Hosting;

/// <summary>
/// Owns the primary/alternate host startup orchestration:
/// <list type="bullet">
///   <item>Invokes the configured <see cref="IMohistDatabaseInitializer"/>
///   on every host attempt (primary and any alternate) before
///   <c>StartAsync</c>.</item>
///   <item>Initializes <see cref="RuntimeObservability"/> from the plan's
///   initial <see cref="CollectorResult"/> by replacing the registered
///   singleton with one whose ordered seed list expresses that state,
///   so the alternate's <c>latest_degradation</c> resolves to
///   <c>collector_bind_failed</c> immediately after construction.</item>
///   <item>Promotes a successful primary <c>StartAsync</c> to
///   <see cref="CollectorResult.Online"/> and proceeds to
///   <see cref="IMohistHost.WaitForShutdownAsync"/>.</item>
///   <item>On a classified bind failure, awaits <c>StopAsync</c>,
///   attempts <c>DisposeAsync</c>, and only builds the alternate after
///   both succeed; otherwise rethrows the single failure or surfaces an
///   <see cref="AggregateException"/> preserving stop-then-dispose
///   order.</item>
///   <item>Treats a database initialization failure as terminal: it never
///   invokes <c>StartAsync</c>, never builds an alternate, and always
///   disposes the unstarted host, with an
///   <see cref="AggregateException"/> preserving
///   initialization-then-disposal order when both throw.</item>
/// </list>
/// </summary>
public sealed class MohistHostRunner
{
    private readonly IMohistHostFactory _factory;
    private readonly IOtelBindFailureClassifier _classifier;
    private readonly IMohistDatabaseInitializer _databaseInitializer;
    private readonly ILogger<MohistHostRunner>? _logger;

    public MohistHostRunner(
        IMohistHostFactory factory,
        IOtelBindFailureClassifier classifier,
        IMohistDatabaseInitializer databaseInitializer,
        ILogger<MohistHostRunner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(databaseInitializer);
        _factory = factory;
        _classifier = classifier;
        _databaseInitializer = databaseInitializer;
        _logger = logger;
    }

    /// <summary>
    /// Run the primary host attempt; on classified bind failure run the
    /// alternate from the same epoch. Re-entrant callers should not
    /// invoke this twice on the same plans.
    /// </summary>
    public async Task RunAsync(MohistHostPlan primaryPlan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(primaryPlan);

        var primary = _factory.CreatePrimary(primaryPlan);

        // Primary initialization runs before primary start.
        var primaryInit = await SafeInitializeAsync(primary, "primary", cancellationToken).ConfigureAwait(false);
        if (primaryInit.Kind == InitializationOutcomeKind.Failed)
        {
            await SafeDisposeAsync(primary).ConfigureAwait(false);
            if (primaryInit.InitializationError is { } init && primaryInit.DisposalError is { } disp)
            {
                throw new AggregateException(init, disp)
                {
                    Data = { ["host_role"] = "primary" },
                };
            }
            throw primaryInit.InitializationError!;
        }

        Exception? startup = null;
        try
        {
            await primary.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            startup = ex;
        }

        if (startup is null)
        {
            _logger?.LogInformation("Mohist primary host started successfully");

            await OnPrimaryStartedAsync(primary, primaryPlan).ConfigureAwait(false);
            try
            {
                await primary.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await SafeDisposeAsync(primary).ConfigureAwait(false);
            }
            return;
        }

        // Start failed. Decide: bind-failure classification or hard.
        var decision = _classifier.Classify(startup, primaryPlan);
        if (decision.Result is null)
        {
            await SafeDisposeAsync(primary).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startup).Throw();
            return;
        }

        _logger?.LogInformation("Mohist primary host bind failure; starting alternate host");

        await RunAlternateAfterBindFailureAsync(primary, primaryPlan, startup, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Build the alternate after a classified bind failure on the
    /// primary. Awaits <c>StopAsync</c>, attempts <c>DisposeAsync</c>;
    /// only proceeds to the alternate when both succeed. Otherwise
    /// rethrows the single failure or surfaces an
    /// <see cref="AggregateException"/> preserving stop-then-dispose
    /// order.
    /// </summary>
    private async Task RunAlternateAfterBindFailureAsync(
        IMohistHost primary,
        MohistHostPlan primaryPlan,
        Exception startupException,
        CancellationToken cancellationToken)
    {
        Exception? stopError = null;
        try
        {
            await primary.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopError = ex;
        }

        Exception? disposeError = null;
        try
        {
            await primary.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            disposeError = ex;
        }

        if (stopError is not null || disposeError is not null)
        {
            var inner = new List<Exception>(3) { startupException };
            if (stopError is not null)
                inner.Add(stopError);
            if (disposeError is not null)
                inner.Add(disposeError);

            if (inner.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner[0]).Throw();
            else
                throw new AggregateException(inner);
        }

        var alternatePlan = MohistHostPlan.Alternate(primaryPlan);
        var alternate = _factory.CreateAlternate(alternatePlan);

        // Dispose the alternate cleanly even if its own initialization
        // or start fails; no further fallback follows the alternate.
        try
        {
            var alternateInit = await SafeInitializeAsync(alternate, "alternate", cancellationToken)
                .ConfigureAwait(false);
            if (alternateInit.Kind == InitializationOutcomeKind.Failed)
            {
                await SafeDisposeAsync(alternate).ConfigureAwait(false);
                if (alternateInit.InitializationError is { } init && alternateInit.DisposalError is { } disp)
                {
                    throw new AggregateException(init, disp)
                    {
                        Data = { ["host_role"] = "alternate" },
                    };
                }
                throw alternateInit.InitializationError!;
            }

            await alternate.StartAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await alternate.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await SafeDisposeAsync(alternate).ConfigureAwait(false);
            }
        }
        catch
        {
            await SafeDisposeAsync(alternate).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns <see cref="InitializationOutcomeKind.Succeeded"/> when
    /// database initialization succeeds; otherwise
    /// <see cref="InitializationOutcomeKind.Failed"/> with both
    /// errors captured.
    /// </summary>
    private async Task<InitializationOutcome> SafeInitializeAsync(
        IMohistHost host,
        string role,
        CancellationToken cancellationToken)
    {
        try
        {
            await _databaseInitializer.InitializeAsync(host.Services, cancellationToken)
                .ConfigureAwait(false);
            return InitializationOutcome.Succeeded();
        }
        catch (Exception initializationError)
        {
            Exception? disposalError = null;
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                disposalError = ex;
            }

            return InitializationOutcome.Failed(initializationError, disposalError);
        }
    }

    private static async Task OnPrimaryStartedAsync(IMohistHost primary, MohistHostPlan plan)
    {
        if (!plan.Enabled || plan.ListenerIntent is null)
            return;

        if (primary.Services.GetService(typeof(RuntimeObservability)) is RuntimeObservability runtime)
            runtime.PublishCollector(CollectorResult.Online());

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task SafeDisposeAsync(IMohistHost host)
    {
        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private enum InitializationOutcomeKind { Succeeded, Failed }

    private readonly record struct InitializationOutcome(
        InitializationOutcomeKind Kind,
        Exception? InitializationError = null,
        Exception? DisposalError = null)
    {
        public static InitializationOutcome Succeeded() =>
            new(InitializationOutcomeKind.Succeeded);

        public static InitializationOutcome Failed(Exception initializationError, Exception? disposalError) =>
            new(InitializationOutcomeKind.Failed, initializationError, disposalError);
    }
}
