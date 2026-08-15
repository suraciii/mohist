using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Data.Project;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    public DbSet<CredentialProjectGrantRow> CredentialProjectGrants { get; set; } = null!;

    private static void ConfigureAdditionalModels(ModelBuilder modelBuilder)
    {
        WorkflowAttentionModelConfiguration.Apply(modelBuilder);

        modelBuilder.Entity<CredentialRow>(entity =>
        {
            entity.Property(e => e.DirectApiProjectGrantKind).HasMaxLength(32);
        });

        modelBuilder.Entity<AgentJobRow>(entity =>
        {
            entity.Property(e => e.DirectApiProjectionJson);
            entity.Property(e => e.DirectApiProjectionRevision);
        });

        modelBuilder.Entity<CredentialProjectGrantRow>(entity =>
        {
            entity.ToTable("CredentialProjectGrants");
            entity.HasKey(e => new { e.CredentialId, e.ProjectId });
            entity.Property(e => e.CredentialId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ProjectId).HasMaxLength(256).IsRequired();
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("IX_CredentialProjectGrants_ProjectId");
            entity.HasIndex(e => new { e.CredentialId, e.ProjectId })
                .IsUnique()
                .HasDatabaseName("UX_CredentialProjectGrants_CredentialId_ProjectId");
            entity.HasOne<CredentialRow>()
                .WithMany()
                .HasForeignKey(e => e.CredentialId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ProjectRow>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
