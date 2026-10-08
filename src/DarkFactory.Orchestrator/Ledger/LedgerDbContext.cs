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

    /// <summary>The story's epic on its board, if any: the scope an epic-level control applies to.</summary>
    public long? EpicId { get; set; }

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

/// <summary>
/// A worker session that has read untrusted content (E4, <see cref="Worker.Taint"/>): it never gets a push token. Insert-only and keyed
/// by the Claude session id, so it holds across resume and crash; the first reason recorded is kept, and the database refuses to update
/// or delete a row.
/// </summary>
public sealed class SessionTaint
{
    public required string ClaudeSessionId { get; set; }

    /// <summary>The item the session worked for, when known.</summary>
    public long? WorkItemId { get; set; }

    /// <summary>What tainted it: <c>issue-text</c>, <c>outsider-comment</c>, <c>web:&lt;tool&gt;</c> or <c>mcp:&lt;tool&gt;</c>.</summary>
    public required string Reason { get; set; }

    public DateTimeOffset TaintedAt { get; set; }
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

/// <summary>
/// A Pause/Continue/Stop control at one scope: <c>factory</c>, <c>epic:&lt;id&gt;</c> or <c>item:sc-&lt;id&gt;</c>
/// (<see cref="Controls.ControlScope"/>). Written by the dashboard and the CLI; read by every pipeline and the
/// intake loop, whichever process they run in.
/// </summary>
public sealed class Control
{
    public required string Scope { get; set; }
    public Controls.ControlState State { get; set; }
    public required string ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Usage scope only: why the factory is paused (<see cref="Controls.UsagePause"/>).</summary>
    public string? Reason { get; set; }

    /// <summary>Usage scope only: when the pause lifts by itself (it pauses nothing from then on).</summary>
    public DateTimeOffset? ResumeAt { get; set; }

    /// <summary>Usage scope only: the backoff that set <see cref="ResumeAt"/>, when no reset time was known; doubles on a repeat.</summary>
    public TimeSpan? Backoff { get; set; }

    /// <summary>
    /// Postgres's <c>xmin</c> as a concurrency token: a write that raced another writer's fails and is decided again
    /// (<see cref="Controls.LedgerControls"/>), so e.g. a worker's short backoff cannot overwrite a known reset.
    /// </summary>
    public uint Version { get; set; }

    /// <summary>Whether this control pauses its scope at <paramref name="now"/>: a usage pause lifts at its <see cref="ResumeAt"/>.</summary>
    public bool PausesAt(DateTimeOffset now) => State == Controls.ControlState.Paused && (ResumeAt is null || now < ResumeAt);
}

/// <summary>
/// A GitHub issue the factory has seen in a watched repo, and the key (<see cref="Id"/>) its work item is named by
/// (<c>gh-&lt;Id&gt;</c>, <see cref="WorkSources.ItemNaming.GitHubIssue"/>): issue numbers repeat across repos.
/// Everything the factory decides about the issue is a ledger row of that work item.
/// </summary>
public sealed class GitHubIssue
{
    public int Id { get; set; }

    /// <summary>The issue's repo, <c>owner/name</c>.</summary>
    public required string Repo { get; set; }

    public int Number { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
}

/// <summary>How far the issue poll of one watched repo has read (issues updated at or after <see cref="Since"/> are read again).</summary>
public sealed class GitHubIssueCursor
{
    public required string Repo { get; set; }

    public DateTimeOffset Since { get; set; }
}

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<GitHubIssue> GitHubIssues => Set<GitHubIssue>();
    public DbSet<GitHubIssueCursor> GitHubIssueCursors => Set<GitHubIssueCursor>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<WorkerSession> WorkerSessions => Set<WorkerSession>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<Control> Controls => Set<Control>();
    public DbSet<SessionTaint> SessionTaints => Set<SessionTaint>();

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
        modelBuilder.Entity<Control>(e =>
        {
            e.ToTable("controls");
            e.HasKey(x => x.Scope);
            e.Property(x => x.Scope).HasMaxLength(64);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ChangedBy).HasMaxLength(128);
            e.Property(x => x.Reason).HasMaxLength(64);
            e.Property(x => x.Version).IsRowVersion();
        });
        modelBuilder.Entity<GitHubIssue>(e =>
        {
            e.ToTable("github_issues");
            e.Property(x => x.Repo).HasMaxLength(200);
            e.HasIndex(x => new { x.Repo, x.Number }).IsUnique();
        });
        modelBuilder.Entity<SessionTaint>(e =>
        {
            e.ToTable("session_taints");
            e.HasKey(x => x.ClaudeSessionId);
            e.Property(x => x.ClaudeSessionId).HasMaxLength(128);
            e.Property(x => x.Reason).HasMaxLength(128);
            e.HasIndex(x => x.WorkItemId);
            e.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId);
        });
        modelBuilder.Entity<GitHubIssueCursor>(e =>
        {
            e.ToTable("github_issue_cursors");
            e.HasKey(x => x.Repo);
            e.Property(x => x.Repo).HasMaxLength(200);
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
