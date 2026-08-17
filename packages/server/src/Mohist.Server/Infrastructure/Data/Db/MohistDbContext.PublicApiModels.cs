using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.PublicApi;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    public DbSet<PublicExecutionSnapshotRow> PublicExecutionSnapshots { get; set; } = null!;
    public DbSet<PublicSessionEventRow> PublicSessionEvents { get; set; } = null!;
    public DbSet<PublicStreamStateRow> PublicStreamStates { get; set; } = null!;
    public DbSet<PublicProjectionCheckpointRow> PublicProjectionCheckpoints { get; set; } = null!;

    private static void ConfigurePublicApiModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PublicExecutionSnapshotRow>(entity =>
        {
            entity.ToTable("public_execution_snapshots");
            entity.HasKey(e => new { e.AnchorType, e.AnchorId });
            entity.Property(e => e.AnchorType).HasMaxLength(16).IsRequired();
            entity.Property(e => e.AnchorId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AgentId).HasMaxLength(256);
            entity.Property(e => e.SessionId).HasMaxLength(256);
            entity.Property(e => e.SnapshotJson).IsRequired();
            entity.Property(e => e.TerminalFact).HasMaxLength(512);
            entity.Property(e => e.TerminalOutcome).HasMaxLength(32);
            entity.Property(e => e.TerminalAt).HasMaxLength(64);
            entity.HasIndex(e => e.SessionId)
                .HasDatabaseName("IX_public_execution_snapshots_SessionId");
            entity.HasIndex(e => new { e.SessionId, e.LastSequence })
                .HasDatabaseName("IX_public_execution_snapshots_SessionId_LastSequence");
        });

        modelBuilder.Entity<PublicSessionEventRow>(entity =>
        {
            entity.ToTable("public_session_events");
            entity.HasKey(e => new { e.SessionId, e.Generation, e.Sequence });
            entity.Property(e => e.SessionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Sequence);
            entity.Property(e => e.Type).HasMaxLength(64).IsRequired();
            entity.Property(e => e.OccurredAt).HasMaxLength(64).IsRequired();
            entity.Property(e => e.PayloadJson).IsRequired();
            entity.Property(e => e.SourceTransition).HasMaxLength(512).IsRequired();
            // One public sequence per normalized source transition per
            // generation: the replay-deduplication contract for crash
            // recovery and rebuilds.
            entity.HasIndex(e => new { e.SessionId, e.Generation, e.SourceTransition })
                .IsUnique()
                .HasDatabaseName("UX_public_session_events_Transition");
            entity.HasIndex(e => new { e.SessionId, e.Generation, e.Sequence })
                .HasDatabaseName("IX_public_session_events_SessionId_Generation_Sequence");
        });

        modelBuilder.Entity<PublicStreamStateRow>(entity =>
        {
            entity.ToTable("public_stream_states");
            entity.HasKey(e => e.SessionId);
            entity.Property(e => e.SessionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.EarliestSequence);
            entity.Property(e => e.LatestSequence);
        });

        modelBuilder.Entity<PublicProjectionCheckpointRow>(entity =>
        {
            entity.ToTable("public_projection_checkpoints");
            entity.HasKey(e => new { e.Feed, e.SourceKey });
            entity.Property(e => e.Feed).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SourceKey).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Watermark).HasMaxLength(128).IsRequired();
        });
    }
}
