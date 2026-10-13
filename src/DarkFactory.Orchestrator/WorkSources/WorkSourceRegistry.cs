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
/// <param name="Settings">The settings that configure it, as a start-up refusal names them.</param>
/// <param name="Watched">Whether its watch scope is set: the source is then polled by <c>factory work</c>.</param>
/// <param name="HasCredential">Whether its credential resolves: an item of it can then be run, paused or stopped by id.</param>
/// <param name="Http">The source's outbound client (<see cref="OutboundHttp"/>), owned by the registry that asked for it.</param>
/// <param name="Unwatched">
/// <c>factory work</c>'s start-up note when the source is not watched, or null for none (a source no setting names is not mentioned).
/// </param>
/// <param name="Prepare">
/// What <c>factory work</c> runs before each listing of the source while the factory is not paused (<see cref="IntakeLane.Prepare"/>),
/// or null.
/// </param>
public sealed record WorkSourceKind(
    ItemNaming Naming,
    string Name,
    string Settings,
    Func<FactoryOptions, bool> Watched,
    Func<FactoryOptions, bool> HasCredential,
    Func<FactoryOptions, HttpClient> Http,
    Func<FactoryOptions, HttpClient, IDbContextFactory<LedgerDbContext>, IWorkSource> Create,
    Func<FactoryOptions, string?> Unwatched,
    Func<FactoryOptions, IntakeStatus, TextWriter, Func<CancellationToken, Task>>? Prepare = null);

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
/// A source is built once, on first use, with its own outbound client; the registry owns the clients and disposes them.
/// </summary>
/// <param name="http">Tests: the client of a source's fake board, in place of <see cref="WorkSourceKind.Http"/>.</param>
/// <param name="contexts">Tests: the ledger, in place of the configured Postgres one.</param>
public sealed class WorkSourceRegistry(FactoryOptions options, Func<ItemNaming, HttpClient>? http = null,
    IDbContextFactory<LedgerDbContext>? contexts = null) : IDisposable
{
    /// <summary>Every kind of source, one per <see cref="ItemNaming.All"/> entry, in the order the intake loop polls them.</summary>
    public static readonly IReadOnlyList<WorkSourceKind> Kinds =
    [
        new(ItemNaming.Shortcut, "Shortcut",
            "a Shortcut API token (env SHORTCUT_API_TOKEN, config 'Shortcut:ApiToken' or keychain account "
            + $"'{SecretAccounts.ShortcutApiToken}') and a watch scope (Shortcut:Watch:Teams and/or Shortcut:Watch:Epics)",
            o => !o.WatchScope.IsEmpty,
            o => o.TryGet(x => x.ShortcutApiToken, out _),
            _ => OutboundHttp.ShortcutApi(),
            (o, client, _) => Shortcut(o, client),
            o => o.TryGet(x => x.ShortcutApiToken, out _)
                ? "Shortcut: the watch scope is empty (set Shortcut:Watch:Teams and/or Shortcut:Watch:Epics); no Shortcut story will be picked up."
                : null),
        new(ItemNaming.GitHubIssue, "GitHub issues",
            "the repositories whose issues are triaged (GitHub:Watch:Repos; the factory's GitHub App is needed in any case)",
            o => o.WatchedIssueRepos.Count > 0,
            o => o.TryGet(x => x.GitHubAppId, out _) && o.TryGet(x => x.GitHubAppPrivateKeyPem, out _),
            _ => OutboundHttp.GitHubApi(),
            Issues,
            _ => "No GitHub issues are watched (set GitHub:Watch:Repos to triage a repo's issues).",
            // Triage new issues and record approvals, so the items they release are listed by the poll that follows.
            (o, status, log) => ct => FactoryRunner.PollIssuesAsync(o, status, log, ct)),
        new(ItemNaming.Kanban, "KanbanBoard",
            "a KanbanBoard write token (env FACTORY_KANBAN_WRITE_TOKEN, config 'Kanban:WriteToken' or keychain account "
            + $"'{SecretAccounts.KanbanWriteToken}') and the watched projects ({KanbanScope.Setting})",
            o => !o.KanbanWatchScope.IsEmpty,
            o => o.TryGet(x => x.KanbanWriteToken, out _),
            o => OutboundHttp.KanbanApi(o.KanbanMcpUrl),
            Kanban,
            o => o.TryGet(x => x.KanbanWriteToken, out _)
                ? $"KanbanBoard: no project is watched (set {KanbanScope.Setting}); no Kanban item will be picked up."
                : null),
    ];

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
        new(new GitHubIssuesClient(githubHttp, new GitHubApp(githubHttp, options.GitHubAppId, options.GitHubAppPrivateKeyPem, TimeProvider.System)),
            contexts ?? Ledger(options), options.WatchedIssueRepos);

    /// <summary>
    /// The Kanban work source over the watched projects (<c>Kanban:Watch:Projects</c>; sc-25981), with the orchestrator's own
    /// full-scope token (<c>Kanban:WriteToken</c>) in the one client built for it (<see cref="KanbanWrites.Client"/>) over
    /// <paramref name="kanbanHttp"/> (<see cref="OutboundHttp.KanbanApi"/>). That client is apart from the Kanban upstream of
    /// <see cref="FactoryRunner.CreateUpstreams"/>, which holds the read token and is all a model session reaches (E2).
    /// </summary>
    public static KanbanWorkSource Kanban(FactoryOptions options, HttpClient kanbanHttp, IDbContextFactory<LedgerDbContext>? contexts = null) =>
        new(new KanbanBoard(KanbanWrites.Client(kanbanHttp, options.KanbanWriteToken)), new KanbanKeys(contexts ?? Ledger(options)),
            options.KanbanWatchScope, options.KanbanWebUrl);

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
    /// One intake lane per configured source, each with the runner of its items (<paramref name="runner"/>): <c>factory work</c>'s
    /// loop polls them in turn, so two sources' runs (and a triage) never hold the single-tenant worker sandbox at once.
    /// </summary>
    public IReadOnlyList<IntakeLane> Lanes(Func<IWorkSource, IItemRunner> runner, IntakeStatus status, TextWriter log) =>
        Configured.Select(s => new IntakeLane(s, runner(s), KindOf(s.Naming).Prepare?.Invoke(options, status, log))).ToList();

    /// <summary>Why <c>factory work</c> and <c>factory run</c> refuse to start with no work source: what would configure one.</summary>
    public static string NoneConfigured =>
        "No work source is configured, so there is nothing the factory could work. Configure at least one: "
        + string.Join("; ", Kinds.Select(k => $"{k.Name} — {k.Settings}")) + ".";

    /// <summary>Why an item of <paramref name="kind"/> cannot be reached, and with no source usable at all, what would configure one.</summary>
    private string NotConfigured(WorkSourceKind kind) =>
        Kinds.Any(k => k.Watched(options) && k.HasCredential(options))
            ? $"The {kind.Name} work source is not configured ({kind.Naming.Prefix}-<n> names a {kind.Naming.Noun}): it needs {kind.Settings}."
            : $"The {kind.Name} work source is not configured ({kind.Naming.Prefix}-<n> names a {kind.Naming.Noun}). {NoneConfigured}";

    private IWorkSource Build(WorkSourceKind kind)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(kind.Naming, out var source))
            {
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
