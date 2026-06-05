namespace Mohist.Server.Infrastructure.Data;

public interface IStateStore<T> where T : class
{
    Task<T?> LoadAsync(string key);
    Task<IReadOnlyList<T>> ListAsync();
    Task SaveAsync(string key, T state);
    Task DeleteAsync(string key);
}
