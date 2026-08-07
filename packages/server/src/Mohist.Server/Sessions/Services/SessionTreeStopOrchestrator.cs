using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

public sealed class SessionTreeStopOrchestrator(IGrainFactory grains) : IScopedService
{
    public Task<SessionTreeStopOperation> StartAsync(
        string projectId,
        string rootSessionId,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(rootSessionId))
            throw new ArgumentException("RootSessionId is required.", nameof(rootSessionId));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(idempotencyKey));

        var operationId = SessionTreeStopOperationIds.For(projectId, rootSessionId, idempotencyKey);
        var request = new SessionTreeStopRequest(
            projectId,
            rootSessionId,
            operationId,
            idempotencyKey,
            $"session-tree-stop:{projectId}:{rootSessionId}");
        return grains
            .GetGrain<ISessionTreeStopOperationGrain>(operationId)
            .StartAsync(request);
    }

    public async Task<SessionTreeStopOperation?> GetAsync(
        string projectId,
        string rootSessionId,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(rootSessionId))
            throw new ArgumentException("RootSessionId is required.", nameof(rootSessionId));
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("OperationId is required.", nameof(operationId));

        SessionTreeStopOperation operation;
        try
        {
            operation = await grains
                .GetGrain<ISessionTreeStopOperationGrain>(operationId)
                .GetAsync();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return operation.ProjectId == projectId
            && operation.RootSessionId == rootSessionId
            ? operation
            : null;
    }
}
