using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Infrastructure.Data.Db;

// Lifecycle-transition mapping split from MohistDbContext to keep the main
// partial within the file-size ratchet.
public partial class MohistDbContext
{
    partial void ConfigureAgentSessionLifecycleTransitions(ModelBuilder modelBuilder);

    partial void ConfigureAgentSessionLifecycleTransitions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentSessionLifecycleTransitionRow>(entity =>
        {
            entity.ToTable("AgentSessionLifecycleTransitions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SessionId).HasMaxLength(512).IsRequired();
            entity.Property(e => e.SourceTransition).HasMaxLength(512).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.AnchorKind).HasMaxLength(16).IsRequired();
            entity.Property(e => e.AnchorId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SnapshotJson).IsRequired();
            entity.Property(e => e.OccurredAt).IsRequired();
            entity.HasIndex(e => new { e.SessionId, e.Id })
                .HasDatabaseName("IX_AgentSessionLifecycleTransitions_SessionId_Id");
        });
    }
}
