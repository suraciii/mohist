using Microsoft.EntityFrameworkCore;
using Mohist.Server.Storage.Db.Entities;

namespace Mohist.Server.Storage.Db;

public class MohistDbContext : DbContext
{
    public DbSet<GrainState> GrainStates { get; set; } = null!;

    private readonly string _dbPath;

    public MohistDbContext()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataDir = Path.Combine(home, ".mohist");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "mohist.db");
    }

    public MohistDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    public MohistDbContext(DbContextOptions<MohistDbContext> options) : base(options)
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataDir = Path.Combine(home, ".mohist");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "mohist.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        }
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
    }
}
