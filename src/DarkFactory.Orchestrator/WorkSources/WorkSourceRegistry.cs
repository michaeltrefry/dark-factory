using DarkFactory.Orchestrator.Gateway;
using DarkFactory.Orchestrator.GitHub;
using DarkFactory.Orchestrator.Issues;
using DarkFactory.Orchestrator.Kanban;
using DarkFactory.Orchestrator.Ledger;
using DarkFactory.Orchestrator.Shortcut;
using Microsoft.EntityFrameworkCore;

namespace DarkFactory.Orchestrator.WorkSources;

/// <summary>
/// One kind of work source the factory knows (sc-25982): how its items are named, which settings configure it, and how it is
/// built. The only place that names a source's own type; everything else reaches a source through <see cref="WorkSourceRegistry"/>
/// and <see cref="IWorkSource"/> (E1).
/// </summary>
/// <param name="Name">What the source is called in start-up messages, e.g. <c>Shortcut</c>.</param>
/// <param name="Credential">Its credential alone, as a refusal names it: all an item named by id needs (<see cref="WorkSourceRegistry.Of"/>).</param>
/// <param name="Settings">Everything that makes it a polled source — the credential and the watch scope — as a start-up refusal names it.</param>
/// <param name="Watched">Whether its watch scope is set: the source is then polled by <c>factory work</c> for new work.</param>
/// <param name="HasCredential">
/// Whether its credential resolves: an item of it can then be run, paused or stopped by id, and <c>factory work</c> finishes the
/// items of it that are already in flight, watched or not (<see cref="WorkSourceRegistry.Lanes"/>).
/// </param>
/// <param name="Http">The source's outbound client (<see cref="OutboundHttp"/>), owned by the registry that asked for it.</param>
/// <param name="Unwatched">
/// <c>factory work</c>'s start-up note when the source is not watched, or null for none (a source no setting names is not mentioned).
/// </param>
/// <param name="Prepare">
/// What <c>factory work</c> runs before each listing of the source while the factory is not paused (<see cref="IntakeLane.Prepare"/>),
/// or null. Only a watched source is prepared.
/// </param>
/// <param name="Links">
/// The source's links to its items for the dashboard, built from the ledger alone (<see cref="IBoardLinks"/>, sc-25985), or null
/// for a source whose items the dashboard shows without one.
/// </param>
/// <param name="KnowsEpic">
/// Whether an epic of this name can exist, answered from the ledger alone (sc-25985) — what <see cref="IWorkSource.KnowsEpicAsync"/>
/// answers, for a caller without the source (its credential is missing, or the host has no registry): an epic control must be
/// refused on a key the ledger never gave out whether or not the board can be reached. Null for a source whose epics are named
/// by their own number on the board (any may exist), or that has none.
/// </param>
public sealed record WorkSourceKind(
    ItemNaming Naming,
    string Name,
    string Credential,
    string Settings,
    Func<FactoryOptions, bool> Watched,
    Func<FactoryOptions, bool> HasCredential,
    Func<FactoryOptions, HttpClient> Http,
    Func<FactoryOptions, HttpClient, IDbContextFactory<LedgerDbContext>, IWorkSource> Create,
    Func<FactoryOptions, string?> Unwatched,
    Func<FactoryOptions, IntakeStatus, TextWriter, Func<CancellationToken, Task>>? Prepare = null,
    Func<FactoryOptions, IBoardLinks>? Links = null,
    Func<IDbContextFactory<LedgerDbContext>, long, CancellationToken, Task<bool>>? KnowsEpic = null,
    bool Internal = false);

/// <summary>
/// The factory's work sources (sc-25982), keyed by <see cref="ItemNaming"/>: the one registry <c>factory work</c>'s intake loop and
/// start-up check, <c>factory run</c>, <c>factory pause|continue|stop</c>, the dashboard's controls and Stop's cascade take a source
/// from. Two questions, answered from the settings alone:
/// <list type="bullet">
/// <item><see cref="Configured"/> — the sources <c>factory work</c> polls: those whose watch scope is set (Shortcut:
/// <c>Shortcut:Watch:Teams</c>/<c>Epics</c>; KanbanBoard: <c>Kanban:Watch:Projects</c>; GitHub issues: <c>GitHub:Watch:Repos</c>). A
/// watched source without its credential is refused (<see cref="MissingCredentialException"/>), never silently left out. The
/// factory starts with any non-empty set of them (E1), and refuses to start with none (<see cref="NoneConfigured"/>).</item>
/// <item><see cref="Of"/> — the source of one item named by id (<c>sc-12</c>, <c>gh-3</c>, <c>kb-4</c>): it needs only that
/// source's credential, not a watch scope (<c>factory run sc-12 --ignore-scope</c> works with none, as it always did).</item>
/// </list>
/// The intake loop's lanes (<see cref="Lanes"/>) are wider than <see cref="Configured"/>: every source that has its credential gets
/// one, watched or not, so work already in the ledger is finished whatever the watch scopes say now.
/// A source is built once, on first use, with its own outbound client; the registry owns the clients and disposes them.
/// </summary>
/// <param name="http">Tests: the client of a source's fake board, in place of <see cref="WorkSourceKind.Http"/>.</param>
/// <param name="contexts">Tests: the ledger, in place of the configured Postgres one.</param>
public sealed class WorkSourceRegistry(FactoryOptions options, Func<ItemNaming, HttpClient>? http = null,
    IDbContextFactory<LedgerDbContext>? contexts = null) : IDisposable
{
    private const string ShortcutCredential =
        "a Shortcut API token (env SHORTCUT_API_TOKEN, config 'Shortcut:ApiToken' or keychain account '" + SecretAccounts.ShortcutApiToken + "')";

    private const string KanbanCredential =
        "a KanbanBoard write token (env FACTORY_KANBAN_WRITE_TOKEN, config 'Kanban:WriteToken' or keychain account '"
        + SecretAccounts.KanbanWriteToken + "')";

    /// <summary>Every kind of source, one per <see cref="ItemNaming.All"/> entry, in the order the intake loop polls them.</summary>
    public static readonly IReadOnlyList<WorkSourceKind> Kinds =
    [
        new(ItemNaming.Shortcut, "Shortcut", ShortcutCredential,
            $"{ShortcutCredential} and a watch scope (Shortcut:Watch:Teams and/or Shortcut:Watch:Epics)",
            o => !o.WatchScope.IsEmpty,
            o => o.TryGet(x => x.ShortcutApiToken, out _),
            _ => OutboundHttp.ShortcutApi(),
            (o, client, _) => Shortcut(o, client),
            o => o.TryGet(x => x.ShortcutApiToken, out _)
                ? "Shortcut: the watch scope is empty (set Shortcut:Watch:Teams and/or Shortcut:Watch:Epics); no Shortcut story will be picked up "
                    + "(those already in flight are still finished)."
                : null),
        new(ItemNaming.GitHubIssue, "GitHub issues",
            $"the factory's GitHub App (config 'GitHub:AppId' and 'GitHub:PrivateKeyPem', or keychain accounts '{SecretAccounts.GitHubAppId}' and "
            + $"'{SecretAccounts.GitHubAppPrivateKey}': `factory github-app setup`)",
            "the repositories whose issues are triaged (GitHub:Watch:Repos; the factory's GitHub App is needed in any case)",
            o => o.WatchedIssueRepos.Count > 0,
            o => o.TryGet(x => x.GitHubAppId, out _) && o.TryGet(x => x.GitHubAppPrivateKeyPem, out _),
            _ => OutboundHttp.GitHubApi(),
            Issues,
            _ => "No GitHub issues are watched (set GitHub:Watch:Repos to triage a repo's issues).",
            // Triage new issues and record approvals, so the items they release are listed by the poll that follows.
            (o, status, log) => ct => FactoryRunner.PollIssuesAsync(o, status, log, ct)),
        new(ItemNaming.Kanban, "KanbanBoard", KanbanCredential,
            $"{KanbanCredential} and the watched projects ({KanbanScope.Setting})",
            o => !o.KanbanWatchScope.IsEmpty,
            o => o.TryGet(x => x.KanbanWriteToken, out _),
            o => OutboundHttp.KanbanApi(o.KanbanMcpUrl),
            Kanban,
            o => o.TryGet(x => x.KanbanWriteToken, out _)
                ? $"KanbanBoard: no project is watched (set {KanbanScope.Setting}); no Kanban item will be picked up "
                    + "(those already in flight are still finished)."
                : null,
            // Needs no credential: the links are built on Kanban:WebUrl from what the ledger recorded.
            Links: o => new KanbanLedgerLinks(o.KanbanWebUrl),
            // Needs none either: an epic key is the ledger's own.
            KnowsEpic: (contexts, key, ct) => new KanbanKeys(contexts).KnowsEpicAsync(key, ct)),
        new(ItemNaming.Recovery, "Recovery", "no external credential", "ledger-backed incident grants",
            _ => false, _ => true, _ => throw new InvalidOperationException("Internal recovery has no HTTP client."),
            (_, _, contexts) => new Recovery.RecoveryWorkSource(contexts, TimeProvider.System), _ => null, Internal: true),
    ];

    /// <summary>
    /// Every source's links for the dashboard (<see cref="WorkSourceKind.Links"/>), whether or not the source is configured: the
    /// dashboard lists the ledger's items of any source.
    /// </summary>
    public static IReadOnlyList<IBoardLinks> BoardLinks(FactoryOptions options) =>
        Kinds.Select(k => k.Links?.Invoke(options)).OfType<IBoardLinks>().ToList();

    private readonly Dictionary<ItemNaming, IWorkSource> _sources = [];
    private readonly List<HttpClient> _clients = [];
    private readonly object _gate = new();

    /// <summary>The kind of the source that names its items <paramref name="naming"/>.</summary>
    public static WorkSourceKind KindOf(ItemNaming naming) =>
        Kinds.FirstOrDefault(k => k.Naming == naming) ?? throw new ArgumentException($"No work source names its items '{naming.Prefix}-<n>'.", nameof(naming));

    /// <summary>The Shortcut board over the watched teams and epics, with the Shortcut API token.</summary>
    public static IWorkSource Shortcut(FactoryOptions options, HttpClient shortcutHttp) =>
        new ShortcutWorkSource(shortcutHttp, options.ShortcutApiToken, options.WatchScope);

    /// <summary>The GitHub issue work source over the watched repos (<c>GitHub:Watch:Repos</c>), as the workers' App.</summary>
    public static GitHubIssueWorkSource Issues(FactoryOptions options, HttpClient githubHttp, IDbContextFactory<LedgerDbContext>? contexts = null) =>
        new(new GitHubIssuesClient(githubHttp, new GitHubApp(githubHttp, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System, reservedController: options.GuiWorkflow?.Collector)),
            contexts ?? Ledger(options), options.WatchedIssueRepos);

    /// <summary>
    /// The Kanban work source over the watched projects (<c>Kanban:Watch:Projects</c>; sc-25981), with the orchestrator's own
    /// full-scope token (<c>Kanban:WriteToken</c>) in the one client built for it (<see cref="KanbanWrites.Client"/>) over
    /// <paramref name="kanbanHttp"/> (<see cref="OutboundHttp.KanbanApi"/>). That client is apart from the Kanban upstream of
    /// <see cref="FactoryRunner.CreateUpstreams"/>, which holds the read token and is all a model session reaches (E2).
    /// </summary>
    public static KanbanWorkSource Kanban(FactoryOptions options, HttpClient kanbanHttp, IDbContextFactory<LedgerDbContext>? contexts = null)
    {
        EnsureKanbanTokensApart(options);
        return new(new KanbanBoard(KanbanWrites.Client(kanbanHttp, options.KanbanWriteToken)), new KanbanKeys(contexts ?? Ledger(options)),
            options.KanbanWatchScope, options.KanbanWebUrl);
    }

    /// <summary>
    /// Refuses <c>Kanban:WriteToken</c> equal to <c>Kanban:Token</c> (sc-25992, E2): the read token is handed to the loopback MCP
    /// proxy that model sessions call, so the same full-scope token there would give them the orchestrator's writes. Checked at
    /// start-up (<see cref="FactoryOptions.ValidateSettings"/>) and again before the work source is built, whenever both are set,
    /// from whichever source each comes; the message names the keys, never either token.
    /// </summary>
    public static void EnsureKanbanTokensApart(FactoryOptions options)
    {
        if (options.TryGet(o => o.KanbanToken, out var read) && options.TryGet(o => o.KanbanWriteToken, out var write)
            && string.Equals(read, write, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Kanban:WriteToken must not be the same token as Kanban:Token: the read token reaches model sessions through the MCP "
                + "proxy, so it must be a separate read-scope KanbanBoard token (the write token is the factory's own full-scope one).");
        }
    }

    private static LedgerDbContextFactory Ledger(FactoryOptions options) => new(LedgerDbContext.PostgresOptions(options.LedgerConnectionString));

    /// <summary>
    /// The sources <c>factory work</c> polls, in <see cref="Kinds"/> order: every source whose watch scope is set.
    /// </summary>
    /// <exception cref="MissingCredentialException">A watched source's credential is missing.</exception>
    public IReadOnlyList<IWorkSource> Configured => Kinds.Where(k => k.Watched(options)).Select(k => Build(k)).ToList();

    /// <summary><c>factory work</c>'s start-up notes: one line per source that has its credential and watches nothing.</summary>
    public IReadOnlyList<string> Unwatched => Kinds.Where(k => !k.Watched(options)).Select(k => k.Unwatched(options)).OfType<string>().ToList();

    /// <summary>
    /// The source of an item named by id (<c>factory run</c>, <c>factory stop --item</c>, an item's ledger row), whether or not it
    /// is watched.
    /// </summary>
    /// <exception cref="MissingCredentialException">The source's credential is missing; the message names its settings.</exception>
    public IWorkSource Of(ItemNaming naming)
    {
        var kind = KindOf(naming);
        return kind.HasCredential(options) ? Build(kind) : throw new MissingCredentialException(NotConfigured(kind));
    }

    /// <summary><see cref="Of"/>, or null when the source's credential is missing.</summary>
    public IWorkSource? Find(ItemNaming naming) => KindOf(naming) is var kind && kind.HasCredential(options) ? Build(kind) : null;

    /// <summary>
    /// <c>factory work</c>'s intake lanes, in <see cref="Kinds"/> order, each with the runner of its items (<paramref name="runner"/>):
    /// the loop lists them in turn; two sources' runs (and a triage) share the worker slots, each a single-tenant sandbox user
    /// used by one worker phase at a time through its lock (<see cref="WorkerLock"/>, sc-25988; <see cref="Worker.WorkerSlots"/>, sc-25989). One
    /// lane per source that is watched (<see cref="Configured"/>) — and one per source that only has its credential: the ledger may
    /// hold items of it from before its watch scope was emptied, and without a lane no poll would finish their Stop, retry their
    /// closeout, evaluate a waiting plan or resume a pause (E9). An unwatched source's lane picks up nothing new: its
    /// <see cref="IWorkSource.ListReadyAsync"/> answers nothing without asking its board (each source's own rule for an empty scope),
    /// and it is never prepared — no issue is triaged for an issues source that watches no repository. A source without its
    /// credential gets no lane (<see cref="UnreachableAsync"/> says what that leaves behind).
    /// </summary>
    /// <exception cref="MissingCredentialException">A watched source's credential is missing.</exception>
    public IReadOnlyList<IntakeLane> Lanes(Func<IWorkSource, IItemRunner> runner, IntakeStatus status, TextWriter log) =>
        Kinds.Where(k => k.Watched(options) || k.HasCredential(options))
            .Select(k => (Kind: k, Source: Build(k)))
            .Select(l => new IntakeLane(l.Source, runner(l.Source), l.Kind.Watched(options) ? l.Kind.Prepare?.Invoke(options, status, log) : null))
            .ToList();

    /// <summary>
    /// <c>factory work</c>'s start-up warnings, read from the ledger: one line per source that has no credential while the ledger
    /// still holds active (not Done, not Cancelled) items of it. No lane runs those items (<see cref="Lanes"/>), so none is
    /// finished, stopped or resumed until the credential is back; the line names the source, how many, and the credential.
    /// </summary>
    public async Task<IReadOnlyList<string>> UnreachableAsync(CancellationToken ct)
    {
        var without = Kinds.Where(k => !k.HasCredential(options)).ToList();
        if (without.Count == 0)
        {
            return [];
        }
        await using var db = await (contexts ?? Ledger(options)).CreateDbContextAsync(ct);
        var active = await db.WorkItems.AsNoTracking()
            .Where(i => i.State != WorkState.Done && i.State != WorkState.Cancelled)
            .GroupBy(i => i.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return without
            .Select(k => (Kind: k, Count: active.FirstOrDefault(a => a.Source == k.Naming.Source)?.Count ?? 0))
            .Where(x => x.Count > 0)
            .Select(x => $"warning: {x.Kind.Name}: the ledger holds {x.Count} active item(s) of it ({x.Kind.Naming.Prefix}-<n>) and this "
                + "source has no credential, so the factory cannot finish, stop or resume them (the dashboard still lists them). "
                + $"To bring them back, set {x.Kind.Credential}.")
            .ToList();
    }

    /// <summary>What would configure a work source for <c>factory work</c>: each source's credential and watch scope.</summary>
    public static string NoneConfigured =>
        "No work source is configured, so there is nothing the factory could work. Configure at least one: "
        + string.Join("; ", Kinds.Where(k => !k.Internal).Select(k => $"{k.Name} — {k.Settings}")) + ".";

    /// <summary>
    /// Why <c>factory work</c> refuses to start when no source is watched (exit 2): <see cref="NoneConfigured"/>, then how work the
    /// factory already has in flight is finished meanwhile — by id, which needs the source's credential and no watch scope.
    /// </summary>
    public static string NothingWatched =>
        NoneConfigured + " An item the factory already works needs no watch scope to be finished by hand: `factory stop --item <id>` "
        + "cancels it, `factory run <id> --ignore-scope` runs it on.";

    /// <summary>Why an item of a source without its credential cannot be reached by id: the credential, which is all it needs.</summary>
    public static string NeedsCredential(ItemNaming naming)
    {
        var kind = KindOf(naming);
        return $"The {kind.Name} work source is not configured ({naming.Prefix}-<n> names a {naming.Noun}): it needs {kind.Credential}.";
    }

    /// <summary>
    /// Why an item of <paramref name="kind"/> cannot be reached by id (<see cref="NeedsCredential"/>) and, when no source is polled
    /// at all, what would configure one.
    /// </summary>
    private string NotConfigured(WorkSourceKind kind) =>
        Kinds.Any(k => k.Watched(options) && k.HasCredential(options))
            ? NeedsCredential(kind.Naming)
            : $"{NeedsCredential(kind.Naming)} {NoneConfigured}";

    private IWorkSource Build(WorkSourceKind kind)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(kind.Naming, out var source))
            {
                if (kind.Internal)
                {
                    // Internal factories never receive or allocate a transport.
                    _sources[kind.Naming] = source = kind.Create(options, null!, contexts ?? Ledger(options));
                    return source;
                }
                var client = http?.Invoke(kind.Naming) ?? kind.Http(options);
                _clients.Add(client);
                // Reads the credential: a missing one throws here, naming where to set it.
                _sources[kind.Naming] = source = kind.Create(options, client, contexts ?? Ledger(options));
            }
            return source;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
            _clients.Clear();
            _sources.Clear();
        }
    }
}
