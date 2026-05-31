using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Persistence;

namespace Mohist.Server.Tests.Support;

public class InMemoryStateStore<T> : IStateStore<T> where T : class
{
    private readonly ConcurrentDictionary<string, T> _data = new();

    public Task<T?> LoadAsync(string key)
    {
        _data.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    public Task SaveAsync(string key, T state)
    {
        _data[key] = state;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key)
    {
        _data.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<T>> ListAsync()
    {
        IReadOnlyList<T> result = _data.Values.ToList();
        return Task.FromResult(result);
    }
}