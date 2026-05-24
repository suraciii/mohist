using Microsoft.EntityFrameworkCore;
using Mohist.Server.Config.Domain;
using Mohist.Server.Storage.Db.Entities;

namespace Mohist.Server.Storage.Db;

public class MohistDbContext : DbContext
{
    public DbSet<GrainState> GrainStates { get; set; } = null!;
    public DbSet<ConfigEntry> Configs { get; set; } = null!;

    public MohistDbContext(DbContextOptions<MohistDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GrainState>(entity =>
        {
            entity.HasKey(e => new { e.Key, e.Type });
            entity.Property(e => e.Key).HasMaxLength(256);
            entity.Property(e => e.Type).HasMaxLength(256);
            entity.Property(e => e.JsonState).IsRequired();
        });

        modelBuilder.Entity<ConfigEntry>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(256);
            entity.Property(e => e.Value).IsRequired();
        });
    }
}
