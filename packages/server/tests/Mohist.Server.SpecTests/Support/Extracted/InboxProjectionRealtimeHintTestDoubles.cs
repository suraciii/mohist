using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Inbox.Subscriptions;
using Mohist.Server.SpecTests.Support;
using Xunit;
namespace Mohist.Server.SpecTests.Support;

internal sealed class RealtimeHintCapturingEventPublisher : IEventPublisher
{
    private readonly List<RecordedPublish> _published = [];

    public IReadOnlyList<RecordedPublish> Published => _published.ToArray();

    public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        _published.Add(new RecordedPublish(
            envelope.Type,
            envelope.Source.ToString(),
            envelope.Subject,
            envelope.Extensions.Count == 0 ? null : new Dictionary<string, string>(envelope.Extensions),
            envelope.Data));
        return Task.CompletedTask;
    }

    public Task PublishAsync<TData>(
        TData data,
        string type,
        string source,
        string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        CancellationToken ct = default)
    {
        JsonElement? element = data is not null
            ? JsonSerializer.SerializeToElement(data, CloudEvent.JsonOptions)
            : null;
        _published.Add(new RecordedPublish(
            type,
            source,
            subject,
            extensions is null ? null : new Dictionary<string, string>(extensions),
            element));
        return Task.CompletedTask;
    }

    public sealed record RecordedPublish(
        string Type,
        string Source,
        string? Subject,
        IReadOnlyDictionary<string, string>? Extensions,
        JsonElement? Data);
}

internal sealed class RealtimeHintThrowingEventPublisher : IEventPublisher
{
    public Task PublishAsync(CloudEvent envelope, CancellationToken ct = default) =>
        throw new InvalidOperationException("simulated hint-publish failure");

    public Task PublishAsync<TData>(
        TData data,
        string type,
        string source,
        string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        CancellationToken ct = default) =>
        throw new InvalidOperationException("simulated hint-publish failure");
}

internal sealed class RealtimeHintFailOnSecondAsyncContextFactory : IDbContextFactory<MohistDbContext>
{
    private readonly IDbContextFactory<MohistDbContext> _inner;
    private int _asyncCalls;

    public RealtimeHintFailOnSecondAsyncContextFactory(IDbContextFactory<MohistDbContext> inner)
    {
        _inner = inner;
    }

    public MohistDbContext CreateDbContext() => _inner.CreateDbContext();

    public Task<MohistDbContext> CreateDbContextAsync(CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _asyncCalls) == 2)
            return Task.FromException<MohistDbContext>(new InvalidOperationException("simulated insert failure"));
        return _inner.CreateDbContextAsync(ct);
    }
}
