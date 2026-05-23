using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db;
using Mohist.Server.Storage.Db.Entities;

namespace Mohist.Server.Storage;

public class EfStateStore<T> : IStateStore<T> where T : class
{
    private readonly IDbContextFactory<MohistDbContext> _contextFactory;
    private readonly string _typeName = typeof(T).FullName!;

    public EfStateStore(IDbContextFactory<MohistDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<T?> LoadAsync(string key)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.GrainStates.FindAsync(key, _typeName);
        if (row is null) return null;
        return JsonSerializer.Deserialize<T>(row.JsonState);
    }

    public async Task SaveAsync(string key, T state)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.GrainStates.FindAsync(key, _typeName);
        if (row is null)
        {
            db.GrainStates.Add(new GrainState
            {
                Key = key,
                Type = _typeName,
                JsonState = JsonSerializer.Serialize(state),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.JsonState = JsonSerializer.Serialize(state);
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string key)
    {
        await using var db = await _contextFactory.CreateDbContextAsync();
        var row = await db.GrainStates.FindAsync(key, _typeName);
        if (row is not null)
        {
            db.GrainStates.Remove(row);
            await db.SaveChangesAsync();
        }
    }
}
