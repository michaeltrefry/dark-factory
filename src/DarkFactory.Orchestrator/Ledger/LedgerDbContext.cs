using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DarkFactory.Orchestrator.Ledger;

/// <summary>One unit of work pulled from a work source (here: a Shortcut story).</summary>
public sealed class WorkItem
{
    public long Id { get; set; }
    public required string Source { get; set; }
    public required string ExternalId { get; set; }
    public required string Title { get; set; }
    public required string Repo { get; set; }
    public WorkState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<LedgerEntry> Entries { get; set; } = [];
}

/// <summary>
/// An append-only ledger row of a <see cref="WorkItem"/>: a state transition, or a
/// checkpoint inside the current state that resume uses to avoid redoing a step.
/// </summary>
public sealed class LedgerEntry
{
    public long Id { get; set; }
    public long WorkItemId { get; set; }
    /// <summary>The item's state: the new state for a transition row, the unchanged state for a checkpoint.</summary>
    public WorkState State { get; set; }

    /// <summary>Null for a state transition; the name of a completed sub-step for a checkpoint row.</summary>
    public string? Step { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
    public string? ClaudeSessionId { get; set; }
    public string? Detail { get; set; }
}

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItem>(e =>
        {
            e.ToTable("work_items");
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.ExternalId).HasMaxLength(64);
            e.Property(x => x.Repo).HasMaxLength(200);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.WorkItemId);
        });
        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Step).HasMaxLength(32);
            e.Property(x => x.ClaudeSessionId).HasMaxLength(128);
            e.HasIndex(x => x.WorkItemId);
        });
    }

    public static DbContextOptions<LedgerDbContext> PostgresOptions(string connectionString) =>
        new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options;
}

/// <summary>Used by <c>dotnet ef</c>; reads the same configuration as the CLI.</summary>
public sealed class LedgerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args) =>
        new(LedgerDbContext.PostgresOptions(FactoryOptions.LoadConfiguration().GetLedgerConnectionString()));
}
