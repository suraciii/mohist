using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Infrastructure.Data.Db;

// Agent retry-operation mapping split from MohistDbContext to keep the main
// partial within the file-size ratchet.
public partial class MohistDbContext
{
    partial void ConfigureAgentRetryOperations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentRetryOperationRow>(entity =>
        {
            entity.ToTable("agent_retry_operations");
            entity.HasKey(row => row.OperationId);
            entity.Property(row => row.OperationId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(row => row.IdempotencyKey).HasMaxLength(512).IsRequired();
            entity.Property(row => row.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(row => row.TurnId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.Kind).HasMaxLength(16).IsRequired();
            entity.Property(row => row.PreAllocatedSessionId).HasMaxLength(512).IsRequired();
            entity.Property(row => row.PreAllocatedInputId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.PreAllocatedTurnId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.State).HasMaxLength(16).IsRequired();
            entity.Property(row => row.ResultState).HasMaxLength(64);
            entity.Property(row => row.ResultText).HasMaxLength(2048);
            entity.Property(row => row.ResultJobKey).HasMaxLength(512);
            entity.Property(row => row.ResultSessionId).HasMaxLength(512);
            entity.Property(row => row.ResultInputId).HasMaxLength(128);
            entity.Property(row => row.ResultTurnId).HasMaxLength(128);
            entity.Property(row => row.CreatedAt).IsRequired();
            entity.Property(row => row.UpdatedAt).IsRequired();
            entity.HasIndex(row => row.IdempotencyKey).IsUnique()
                .HasDatabaseName("UX_agent_retry_operations_IdempotencyKey");
            entity.HasIndex(row => new { row.SessionId, row.TurnId }).IsUnique()
                .HasDatabaseName("UX_agent_retry_operations_SessionId_TurnId");
        });
    }
}
