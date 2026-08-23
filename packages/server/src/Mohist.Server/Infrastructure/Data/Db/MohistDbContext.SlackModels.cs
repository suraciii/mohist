using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Slack;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    private static void ConfigureSlackAmbiguousPromptModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SlackAmbiguousPromptRow>(entity =>
        {
            entity.ToTable("SlackAmbiguousPrompts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.WorkspaceTeamId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.ConversationId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.MessageTs).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ThreadTs).HasMaxLength(64);
            entity.Property(e => e.WinningConnectionId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.MentionedConnectionIdsJson).IsRequired();
            entity.Property(e => e.SenderSlackUserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.TaskText).IsRequired();
            entity.Property(e => e.FilesJson).IsRequired();
            entity.Property(e => e.AmbiguityKind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CandidateReferencesJson).IsRequired();
            entity.Property(e => e.SelectionState).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ChosenProjectId).HasMaxLength(256);
            entity.Property(e => e.ChosenConnectionId).HasMaxLength(256);
            entity.Property(e => e.DispatchKind).HasMaxLength(32);
            entity.Property(e => e.SelectionSessionId).HasMaxLength(512);
            entity.Property(e => e.SelectionInputId).HasMaxLength(128);
            entity.Property(e => e.SelectionTurnId).HasMaxLength(128);
            entity.Property(e => e.SettleReason).HasMaxLength(256);
            entity.Property(e => e.PromptedAt).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => new { e.WorkspaceTeamId, e.ConversationId, e.MessageTs })
                .IsUnique()
                .HasDatabaseName("UX_SlackAmbiguousPrompts_WorkspaceTeamId_ConversationId_MessageTs");
            entity.HasIndex(e => new { e.ProjectId, e.UpdatedAt })
                .HasDatabaseName("IX_SlackAmbiguousPrompts_ProjectId_UpdatedAt");
            entity.HasIndex(e => new { e.ProjectId, e.SelectionState, e.UpdatedAt })
                .HasDatabaseName("IX_SlackAmbiguousPrompts_ProjectId_SelectionState_UpdatedAt");
        });
    }
}
