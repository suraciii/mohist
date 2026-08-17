using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.DirectApi;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    public DbSet<DirectApiIdempotencyMappingRow> DirectApiIdempotencyMappings { get; set; } = null!;

    private static void ConfigureDirectApiModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DirectApiIdempotencyMappingRow>(entity =>
        {
            entity.ToTable("direct_api_idempotency_mappings");
            entity.HasKey(row => new { row.Command, row.ScopeKey });
            entity.Property(row => row.Command).HasMaxLength(32).IsRequired();
            entity.Property(row => row.ScopeKey).HasMaxLength(1024).IsRequired();
            entity.Property(row => row.CallerKeyId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.Fingerprint).HasMaxLength(64).IsRequired();
            entity.Property(row => row.State).HasMaxLength(16).IsRequired();
            entity.Property(row => row.Outcome);
            entity.Property(row => row.FrozenTarget);
            entity.Property(row => row.TurnId).HasMaxLength(256);
            entity.Property(row => row.CreatedAt).IsRequired();
            entity.Property(row => row.CompletedAt);
            entity.HasIndex(row => new { row.Command, row.ScopeKey })
                .IsUnique()
                .HasDatabaseName("UX_direct_api_idempotency_mappings_Command_ScopeKey");
            entity.HasIndex(row => row.TurnId)
                .IsUnique()
                .HasFilter("\"Command\" = 'stop' AND \"State\" IN ('pending')")
                .HasDatabaseName("UX_direct_api_idempotency_mappings_PendingStop_TurnId");
        });
    }
}
