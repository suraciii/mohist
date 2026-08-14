using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Project.Domain;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    private static void ConfigureProjectWorkflowProfile(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectWorkflowProfile>(entity =>
        {
            entity.ToTable("ProjectWorkflowProfiles");
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.ProjectId).HasMaxLength(256);
            entity.Property(e => e.DefaultTemplateId).HasMaxLength(256);
            entity.Property(e => e.DefaultWorkflowProfileId).HasMaxLength(256);
            entity.Property(e => e.DefaultWorkflowProfileIdKey).HasMaxLength(256);
            entity.Property(e => e.Variables).IsRequired();
            entity.Property(e => e.DisableDefaultIssueTemplate).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.Prompts)
                .HasConversion(
                    value => JSON.Serialize(value),
                    value => JSON.DeserializeDictionary(value))
                .IsRequired()
                .HasDefaultValue(new Dictionary<string, string>());
            entity.Property(e => e.Prompts).Metadata.SetValueComparer(DictionaryStringComparer);

            entity.Property(e => e.AgentActionOverrides)
                .HasConversion(
                    value => JSON.Serialize(value),
                    value => JSON.DeserializeDictionary(value))
                .IsRequired()
                .HasDefaultValue(new Dictionary<string, string>());
            entity.Property(e => e.AgentActionOverrides).Metadata.SetValueComparer(DictionaryStringComparer);

            entity.Property(e => e.DisabledWorkflowProfileIds)
                .HasConversion(
                    value => JSON.Serialize(value),
                    value => JSON.Deserialize<List<string>>(value) ?? new List<string>())
                .IsRequired()
                .HasDefaultValue(new List<string>());
            entity.Property(e => e.DisabledWorkflowProfileIds).Metadata.SetValueComparer(ListStringComparer);
            entity.HasOne<WorkflowProfileRecordRow>()
                .WithMany()
                .HasForeignKey(e => new { e.ProjectId, e.DefaultWorkflowProfileIdKey })
                .HasPrincipalKey(e => new { e.ProjectId, e.ProfileId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
