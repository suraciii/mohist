using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.GitHub;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    public DbSet<GitHubCommandReplyRow> GitHubCommandReplies { get; set; } = null!;

    private static void ConfigureGitHubModels(ModelBuilder modelBuilder)
    {
            modelBuilder.Entity<GitHubConnectionRow>(entity =>
            {
                entity.ToTable("GitHubConnections");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.ProjectId)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.Owner)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.Repo)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.HasIndex(e => new { e.Owner, e.Repo })
                    .IsUnique();
                entity.HasIndex(e => new { e.ProjectId, e.RepositoryName });
                entity.Property(e => e.RepositoryName)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.ApproversJson)
                    .HasColumnType("JSON")
                    .IsRequired();
                entity.Property(e => e.Status)
                    .HasMaxLength(32)
                    .IsRequired();
                entity.Property(e => e.IdentityKind)
                    .HasMaxLength(32)
                    .IsRequired();
                entity.Property(e => e.InstallationId)
                    .HasMaxLength(256);
                entity.Property(e => e.NeedsAttention)
                    .IsRequired();
                entity.Property(e => e.CreatedAt)
                    .IsRequired();
                entity.Property(e => e.UpdatedAt)
                    .IsRequired();
            });

            modelBuilder.Entity<GitHubIssueLinkRow>(entity =>
            {
                entity.ToTable("GitHubIssueLinks");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.ProjectId)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.RepositoryName)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.GithubIssueNumber)
                    .IsRequired();
                entity.Property(e => e.IssueNumber)
                    .IsRequired();
                entity.Property(e => e.MirrorMarker)
                    .HasMaxLength(256);
                entity.Property(e => e.MirrorCreateAttempted)
                    .IsRequired();
                entity.Property(e => e.CommandRequested)
                    .IsRequired();
                entity.HasIndex(e => new { e.ProjectId, e.RepositoryName, e.GithubIssueNumber })
                    .IsUnique()
                    .HasFilter("\"GithubIssueNumber\" > 0");
                entity.HasIndex(e => new { e.ProjectId, e.IssueNumber })
                    .IsUnique();
                entity.Property(e => e.PostedCommentsJson)
                    .HasColumnType("JSON")
                    .IsRequired();
                entity.Property(e => e.StateLabel)
                    .HasMaxLength(256);
                entity.Property(e => e.CreatedAt)
                    .IsRequired();
                entity.Property(e => e.UpdatedAt)
                    .IsRequired();
            });

            modelBuilder.Entity<GitHubCommandReplyRow>(entity =>
            {
                entity.ToTable("GitHubCommandReplies");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.ProjectId)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.ConnectionId)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.RepositoryName)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.GithubIssueNumber)
                    .IsRequired();
                entity.Property(e => e.GithubCommentId)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.Marker)
                    .HasMaxLength(512)
                    .IsRequired();
                entity.Property(e => e.Body)
                    .IsRequired();
                entity.Property(e => e.PostedAt);
                entity.Property(e => e.CreatedAt)
                    .IsRequired();
                entity.Property(e => e.UpdatedAt)
                    .IsRequired();
                entity.HasIndex(e => new { e.ConnectionId, e.GithubIssueNumber, e.GithubCommentId })
                    .IsUnique()
                    .HasDatabaseName("UX_GitHubCommandReplies_Connection_Issue_Comment");
            });

            modelBuilder.Entity<GitHubWriteBackFailureRow>(entity =>
            {
                entity.ToTable("GitHubWriteBackFailures");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.ProjectId)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.ConnectionId)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.RepositoryName)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.GithubIssueNumber)
                    .IsRequired();
                entity.Property(e => e.IssueNumber)
                    .IsRequired();
                entity.Property(e => e.EventType)
                    .HasMaxLength(256)
                    .IsRequired();
                entity.Property(e => e.Operation)
                    .HasMaxLength(32)
                    .IsRequired();
                entity.Property(e => e.ErrorCode)
                    .HasMaxLength(64)
                    .IsRequired();
                entity.Property(e => e.ErrorDetail)
                    .IsRequired();
                entity.HasIndex(e => new { e.ProjectId, e.CreatedAt });
                entity.Property(e => e.CreatedAt)
                    .IsRequired();
            });

    }
}
