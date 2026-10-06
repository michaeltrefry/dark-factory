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

    /// <summary>
    /// Optimistic concurrency token, bumped on every write: a writer holding a stale copy of
    /// the item fails its save instead of overwriting another run's state.
    /// </summary>
    public long Version { get; set; }

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

/// <summary>
/// One Claude Code worker session of a <see cref="WorkItem"/>. A resumed session
/// (<c>claude --resume</c>) continues the same row and event sequence.
/// </summary>
public sealed class WorkerSession
{
    public long Id { get; set; }
    public long WorkItemId { get; set; }

    /// <summary>Claude's session id; null until the worker's stream reports it.</summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>1 for the item's first session, 2 for the next new (not resumed) one, and so on.</summary>
    public int Attempt { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the latest worker run of the session ended; null while one runs.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    public int? ExitCode { get; set; }

    /// <summary><c>succeeded</c>, <c>failed</c>, <c>error</c> or <c>cancelled</c>; null while running.</summary>
    public string? ExitStatus { get; set; }

    /// <summary>The router's committed cost for the session (USD, reporting only, E9); null until fetched.</summary>
    public decimal? CostUsd { get; set; }

    public long? RouterRequestCount { get; set; }
}

/// <summary>One stdout line of a worker session, in arrival order (E7).</summary>
public sealed class SessionEvent
{
    public long Id { get; set; }
    public long WorkerSessionId { get; set; }
    public long WorkItemId { get; set; }

    /// <summary>1-based, gapless within the session, continued across resumes.</summary>
    public long Sequence { get; set; }

    /// <summary>The stream-json <c>type</c>, or <c>raw</c> for a line that is not a JSON object.</summary>
    public required string Type { get; set; }

    /// <summary>The event's <c>subtype</c>, or for messages the first content block's type (e.g. <c>tool_use</c>).</summary>
    public string? Subtype { get; set; }

    /// <summary>The line exactly as the worker wrote it.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<WorkerSession> WorkerSessions => Set<WorkerSession>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItem>(e =>
        {
            e.ToTable("work_items");
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.ExternalId).HasMaxLength(64);
            e.Property(x => x.Repo).HasMaxLength(200);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Version).IsConcurrencyToken();
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
        modelBuilder.Entity<WorkerSession>(e =>
        {
            e.ToTable("worker_sessions");
            e.Property(x => x.ClaudeSessionId).HasMaxLength(128);
            e.Property(x => x.ExitStatus).HasMaxLength(32);
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
            e.HasIndex(x => x.ClaudeSessionId).IsUnique();
            e.HasIndex(x => x.WorkItemId);
            e.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId);
        });
        modelBuilder.Entity<SessionEvent>(e =>
        {
            e.ToTable("session_events");
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Subtype).HasMaxLength(64);
            e.HasIndex(x => new { x.WorkerSessionId, x.Sequence }).IsUnique();
            e.HasIndex(x => x.WorkItemId);
            e.HasOne<WorkerSession>().WithMany().HasForeignKey(x => x.WorkerSessionId);
            e.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId);
        });
    }

    public static DbContextOptions<LedgerDbContext> PostgresOptions(string connectionString) =>
        new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options;
}

/// <summary>Short-lived contexts for writers that run beside the pipeline's own (e.g. session capture).</summary>
public sealed class LedgerDbContextFactory(DbContextOptions<LedgerDbContext> options) : IDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext() => new(options);
}

/// <summary>Used by <c>dotnet ef</c>; reads the same configuration as the CLI.</summary>
public sealed class LedgerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args) =>
        new(LedgerDbContext.PostgresOptions(FactoryOptions.LoadConfiguration().GetLedgerConnectionString()));
}
