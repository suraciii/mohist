using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Events;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    public DbSet<DeadLetterRow> DeadLetters { get; set; } = null!;
    public DbSet<DispatchStreamLeaseRow> DispatchStreamLeases { get; set; } = null!;

    private void ConfigureDispatching(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeadLetterRow>(entity =>
        {
            entity.ToTable("DeadLetters");
            entity.HasKey(e => e.DeadLetterId);
            entity.Property(e => e.DeadLetterId).ValueGeneratedOnAdd();
            entity.Property(e => e.Origin)
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.Source)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.EventId)
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(e => e.Type)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.SpecVersion)
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(e => e.Subject)
                .HasMaxLength(256);
            entity.Property(e => e.DataContentType)
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(e => e.Data)
                .IsRequired()
                .HasColumnType("JSON")
                .HasConversion(
                    data => data.GetRawText(),
                    json => JsonDocument.Parse(json).RootElement.Clone());
            entity.Property(e => e.ExtensionsJson)
                .HasColumnType("JSON")
                .HasConversion(
                    json => json,
                    raw => raw);
            entity.Property(e => e.Time)
                .IsRequired();
            entity.Property(e => e.FailingHandler)
                .HasMaxLength(512)
                .IsRequired();
            entity.Property(e => e.ErrorMessage)
                .IsRequired();
            entity.Property(e => e.AttemptCount)
                .IsRequired();
            entity.Property(e => e.DeadLetteredAt)
                .IsRequired();
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.RedeliveryAttemptedAt);
            entity.Property(e => e.ResolvedAt);
            entity.HasIndex(e => e.DeadLetteredAt);
            entity.HasIndex(e => new { e.FailingHandler, e.DeadLetteredAt });
            entity.HasIndex(e => new { e.Source, e.Id, e.FailingHandler })
                .IsUnique();
        });

        modelBuilder.Entity<DispatchStreamLeaseRow>(entity =>
        {
            entity.ToTable("DispatchStreamLeases");
            entity.HasKey(e => new { e.Origin, e.Source });
            entity.Property(e => e.Origin)
                .HasMaxLength(32)
                .IsRequired();
            entity.Property(e => e.Source)
                .HasMaxLength(256)
                .IsRequired();
            entity.Property(e => e.LeaseOwner)
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(e => e.LeaseUntil)
                .IsRequired();
            entity.Property(e => e.Attempts)
                .IsRequired();
            entity.Property(e => e.LastError);
            entity.Property(e => e.UpdatedAt)
                .IsRequired();
        });
    }
}
