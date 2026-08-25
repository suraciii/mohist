using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Artifacts;
using Mohist.Workflow.Definition;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Shared arrangement for direct-construction WorkflowGrain contract specs.
/// Mirrors the seeding the cluster fixture performed (workflow profile +
/// project default profile rows) without an Orleans silo.
/// </summary>
internal static class WorkflowGrainContractSupport
{
    internal static async Task SeedTemplateAsync(
        MohistDbFixture fixture,
        string projectId,
        WorkflowDefinition definition,
        DateTimeOffset fixedTime)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        const string profileId = "spec/workflow";
        var profile = await db.WorkflowProfileRecords.FindAsync(projectId, profileId);
        if (profile is null)
        {
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = profileId,
                Name = profileId,
                DefinitionSource = WorkflowYamlSerializer.ToYaml(definition),
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim),
            });
        }
        else
        {
            profile.DefinitionSource = WorkflowYamlSerializer.ToYaml(definition);
            profile.UpdatedAt = fixedTime;
        }

        if (await db.ProjectWorkflowProfiles.FindAsync(projectId) is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = profileId,
            });
        }

        await db.SaveChangesAsync();
    }

    internal static WorkflowGrain CreateGrain(
        IServiceProvider services,
        IWorkflowRunStore store,
        string workflowRunId,
        TimeProvider timeProvider)
    {
        var resolver = services.GetRequiredService<WorkflowDefinitionResolver>();
        var identity = GrainTestContext.Create(
            workflowRunId,
            new WorkflowGrainTestProfileCoordinatorFactory(store, resolver));
        return new WorkflowGrain(
            identity.Context,
            identity.Runtime,
            store,
            services.GetRequiredService<IDispatchSnapshotStore>(),
            resolver,
            services.GetRequiredService<WorkflowVariableResolver>(),
            services.GetRequiredService<IWorkflowArtifactBindService>(),
            Options.Create(new WorkflowOptions()),
            timeProvider,
            NullLogger<WorkflowGrain>.Instance);
    }

    /// <summary>
    /// Store wrapper whose event-commit boundary fails when the batch carries
    /// a selected event type; state-only saves and other batches pass through.
    /// Reproduces the cluster fixture's ThrowOnAppend injection at the durable
    /// seam the grain actually commits through.
    /// </summary>
    internal sealed class SelectiveFailingStore : IWorkflowRunStore
    {
        private readonly IWorkflowRunStore _inner;
        private readonly Func<WorkflowEvent, bool> _failBatchWhen;

        public SelectiveFailingStore(IWorkflowRunStore inner, Func<WorkflowEvent, bool> failBatchWhen)
        {
            _inner = inner;
            _failBatchWhen = failBatchWhen;
        }

        public Task<WorkflowRun?> LoadAsync(string workflowRunId, CancellationToken ct = default) =>
            _inner.LoadAsync(workflowRunId, ct);

        public Task SaveAsync(WorkflowRun run, CancellationToken ct = default) => _inner.SaveAsync(run, ct);

        public Task SaveAsync(WorkflowRun run, IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default)
        {
            if (events.Any(_failBatchWhen))
                throw new InvalidOperationException("simulated event save failure");
            return _inner.SaveAsync(run, events, ct);
        }

        public Task DeleteAsync(string workflowRunId, CancellationToken ct = default) =>
            _inner.DeleteAsync(workflowRunId, ct);
    }
}
