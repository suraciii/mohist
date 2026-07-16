using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.SpecTests.Support;

public sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
{
    private readonly DbContextOptions<MohistDbContext> _options;

    public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => _options = options;

    public MohistDbContext CreateDbContext() => new(_options);

    public Task<MohistDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
