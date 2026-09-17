using Microsoft.EntityFrameworkCore;
using SqlRestoreManager.Models;

namespace SqlRestoreManager.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<RestoreHistory> RestoreHistory => Set<RestoreHistory>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<BackupItem> BackupItems => Set<BackupItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Espelha exatamente Database/001_RestoreHistory.sql (DDL é a fonte da verdade).
        modelBuilder.Entity<RestoreHistory>(e =>
        {
            e.ToTable("RestoreHistory", "dbo");
            e.HasKey(x => x.Id).HasName("PK_RestoreHistory");

            e.Property(x => x.Id).UseIdentityColumn();
            e.Property(x => x.JobId).IsRequired();
            e.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.TargetDatabase).HasMaxLength(128).IsRequired();
            e.Property(x => x.SourceDatabase).HasMaxLength(128);
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.CurrentStep).HasMaxLength(200);
            e.Property(x => x.BackupServerName).HasMaxLength(128);
            e.Property(x => x.NodeName).HasMaxLength(128);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.Property(x => x.PostRestoreLog).HasColumnType("nvarchar(max)");
            e.Property(x => x.SafetyBackupPath).HasMaxLength(512);
            e.Property(x => x.StartedAt).HasColumnType("datetime2");
            e.Property(x => x.FinishedAt).HasColumnType("datetime2");
            e.Property(x => x.BackupFinishDate).HasColumnType("datetime2");

            e.HasIndex(x => x.JobId).IsUnique().HasDatabaseName("UX_RestoreHistory_JobId");
            e.HasIndex(x => x.StartedAt).HasDatabaseName("IX_RestoreHistory_StartedAt");
        });

        modelBuilder.Entity<BackupRun>(e =>
        {
            e.ToTable("BackupRun", "dbo");
            e.HasKey(x => x.Id).HasName("PK_BackupRun");
            e.Property(x => x.Trigger).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.CurrentStep).HasMaxLength(300);
            e.Property(x => x.NodeName).HasMaxLength(128);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.Property(x => x.StartedAt).HasColumnType("datetime2");
            e.Property(x => x.FinishedAt).HasColumnType("datetime2");
            e.HasIndex(x => x.RunId).IsUnique().HasDatabaseName("UX_BackupRun_RunId");
            e.HasIndex(x => x.StartedAt).HasDatabaseName("IX_BackupRun_StartedAt");
            e.HasMany(x => x.Items)
             .WithOne()
             .HasForeignKey(x => x.RunId)
             .HasPrincipalKey(x => x.RunId);
        });

        modelBuilder.Entity<BackupItem>(e =>
        {
            e.ToTable("BackupItem", "dbo");
            e.HasKey(x => x.Id).HasName("PK_BackupItem");
            e.Property(x => x.DatabaseName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.FilePath).HasMaxLength(512);
            e.Property(x => x.ErrorMessage).HasMaxLength(4000);
            e.Property(x => x.StartedAt).HasColumnType("datetime2");
            e.Property(x => x.FinishedAt).HasColumnType("datetime2");
            e.Ignore(x => x.DurationSeconds);
            e.HasIndex(x => x.RunId).HasDatabaseName("IX_BackupItem_RunId");
        });
    }
}
