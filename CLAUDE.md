# dark-factory

Autonomous software factory. A deterministic .NET orchestrator (no model decides a
state transition) moves Shortcut stories through a ledgered state machine and runs
Claude Code headless workers through the Weave router.

## Layout

- `src/DarkFactory.Orchestrator` — the `factory` CLI (System.CommandLine), EF Core ledger
  (`Ledger/`, migrations in `Ledger/Migrations`), Shortcut client, GitHub App auth and
  manifest setup (`GitHub/`), git worktrees (`Git/`), Claude worker and its sandbox (`Worker/`), router client,
  session capture + SignalR hub (`Sessions/`), the `factory work` host (`FactoryHost`) and its Blazor Server
  dashboard (`Dashboard/`: login, binding, ledger reads, transcript formatting; components in `Dashboard/Components`),
  `IWorkSource` + the `factory work` intake loop (`WorkSources/`; registered on the `factory work` host by `FactoryHost.BuildWork` via `services.AddIntake(options)`)
  and its Shortcut adapter (`Shortcut/ShortcutWorkSource.cs`), and the Pause/Continue/Stop controls (`Controls/`).
- `scripts/` — `setup-worker-user.sh` (one-time root setup of the `_factory` sandbox user) and
  `factory-worker-launch` (the root-installed helper every sandboxed worker runs through).
- `tests/DarkFactory.Orchestrator.Tests` — unit tests (no network; fake HTTP APIs, InMemory EF, local git).
  `CrashResumeTests` also needs the compose Postgres (skips locally without it, fails under `CI`): it
  SIGKILLs `tests/DarkFactory.CrashHost` (the real pipeline with a fake `claude` script) and restarts it.
  `ShortcutContractTests` replay recorded Shortcut API fixtures (`Fixtures/shortcut`, strict request matching);
  re-record with `SHORTCUT_RECORD=1` (writes only throwaway `[dark-factory fixture]` stories, archived after).
- `tests/DarkFactory.AcceptanceTests` — live epic acceptance harness; skips unless `FACTORY_E2E=1`.

## Commands

```sh
docker compose up -d                          # ledger Postgres on localhost:5434
dotnet tool restore
dotnet build
dotnet test                                   # unit tests; acceptance tests skip
dotnet ef database update --project src/DarkFactory.Orchestrator
dotnet ef migrations add <Name> --project src/DarkFactory.Orchestrator -o Ledger/Migrations
dotnet run --project src/DarkFactory.Orchestrator -- run sc-1234
dotnet run --project src/DarkFactory.Orchestrator -- work          # long-running host: dashboard + session hub on 127.0.0.1 + the intake loop (polls the watch scope)
dotnet run --project src/DarkFactory.Orchestrator -- dashboard set-password   # dashboard login (hash → keychain)
dotnet run --project src/DarkFactory.Orchestrator -- pause --factory          # also continue/stop; --epic N or --item sc-N
dotnet run --project src/DarkFactory.Orchestrator -- github-app setup
dotnet run --project src/DarkFactory.Orchestrator -- github-repo protect owner/name   # rulesets; owner's GH_TOKEN / `gh auth token`
```

## Configuration

Env vars use `__` as the section separator (`Router__BaseUrl`). Secrets are never
committed: they come from env/user-secrets or the macOS login keychain
(service `dark-factory`).

| Key | Default / source |
| --- | --- |
| `Router:BaseUrl` | `http://localhost:8080` |
| `Router:Key` | env `FACTORY_ROUTER_KEY`, or keychain account `router-key` |
| `Shortcut:ApiToken` | env `SHORTCUT_API_TOKEN`, or keychain account `shortcut-api-token` |
| `GitHub:AppId`, `GitHub:PrivateKeyPem` | keychain `github-app-id`, `github-app-private-key` (written by `factory github-app setup`) |
| `Factory:DefaultRepo` | `michaeltrefry/dark-factory-sandbox` (a story line `Repo: owner/name` overrides) |
| `Shortcut:Watch:Teams`, `Shortcut:Watch:Epics` | empty = watch nothing; comma-separated team mention names/ids, epic ids |
| `Intake:PollSeconds` | `60` |
| `Factory:WorkRoot` | `/opt/dark-factory/work` (clones + worktrees; `~/.dark-factory` when `Worker:RunAs=none`) |
| `ConnectionStrings:Ledger` | `Host=localhost;Port=5434;Database=factory;Username=factory;Password=factory` |
| `Worker:ClaudePath` | `claude` (as the worker user sees it: `~_factory/.local/bin` is first on its PATH) |
| `Worker:RunAs` | `_factory` (sandbox user); `none` runs workers as the owner (development only) |
| `Worker:LaunchHelper` | `/usr/local/libexec/dark-factory/factory-worker-launch` |
| `Worker:Auth` | `claude-login` (worker's own Claude login, router passes it through) or `router-key` (router key as Claude's API key; router needs BYOK provider keys) |
| `Worker:TimeoutMinutes` | `30` |
| `Worker:PauseGraceSeconds` | `300` (a paused worker that has not stopped at a tool boundary by then is stopped) |
| `Factory:HostPort` | `47822` (`factory work`: 127.0.0.1, plus `Dashboard:BindAddress`) |
| `Dashboard:BindAddress` | unset = loopback only; one private address (RFC 1918, 100.64/10, fc00::/7) on a local interface |
| `Dashboard:HostName` | extra Host header the dashboard answers to (e.g. MagicDNS name); one plain DNS name, no wildcard/port |
| `Dashboard:PasswordHash` | keychain `dashboard-password-hash` (written by `factory dashboard set-password`) |

Worker sandbox (E5): workers run as the hidden `_factory` user via
`sudo -n -u _factory factory-worker-launch claude …` (one NOPASSWD sudoers rule for that helper only).
The owner's home is ACL-denied to `_factory`, so `~/.ssh`, `~/.config/gh` and the login keychain are
unreachable. Router variables go to the helper on stdin; the helper builds the worker env from scratch
(PATH, HOME from the password database, MSBuild node reuse/build server off, router vars), refuses any
other variable or a program that isn't an absolute path/plain name, and when the worker exits or its stdin
closes kills the tree, the process group and then every other `_factory` process (a `pgrep -U` sweep that
spares the helper's own pid — never `kill -1`, which on macOS kills the sender too; the helper skips that
step when not running as the sandbox user), then exits with the worker's status. So `_factory` is
single-tenant: one sandboxed `factory run` per machine (`WorkerLock`, `<work root>/.factory-run.lock`). Run
`sudo scripts/setup-worker-user.sh` once; the live probe tests (`LiveWorkerSandboxTests`) run only with
`FACTORY_SANDBOX_LIVE=1`, since they kill every `_factory` process (including an interactive `_factory` login).

Secrets in the keychain: `security add-generic-password -w` at its interactive prompt truncates at 128 chars
(Shortcut tokens are longer → 401). Copy the secret, then
`printf 'add-generic-password -U -s dark-factory -a <account> -w %s\n' "$(pbpaste)" | security -i`
(printf is a builtin: nothing in argv) and verify with
`[ "$(security find-generic-password -s dark-factory -a <account> -w)" = "$(pbpaste)" ] && echo stored-ok`.

## Invariants (epic E1–E4, E6)

- The orchestrator touches a board only through `IWorkSource` (E6). Shortcut workflow state ids are resolved by
  name from the API (never hardcoded); "ready" = `To Do` inside the watch scope, minus stories another claimant holds.
  Intake claims (owner + `factory-claimed` label, In Progress) as a `claimed` checkpoint; the PR and branch links
  are a `linked` checkpoint before Review. `ClaimAsync` refuses (no write) unless the fresh story is To Do or already
  ours, in scope (skipped by `factory run --ignore-scope`) and not another owner's, and reads the claim back; a refusal
  parks the item (Paused + `parked` checkpoint). Only Paused rows with detail `interrupted` (`RunPipeline.Interrupted`)
  or `user-paused` (`RunPipeline.UserPaused`, a Pause control) auto-resume (`RunPipeline.InFlightAsync`), the latter only
  once no control pauses the item; a parked item never does. Resumes re-check the scope.
- Controls (`Controls/`): Pause/Continue/Stop rows in the ledger's `controls` table (scope `factory` | `epic:<id>` |
  `item:sc-<id>`; `WorkItem.EpicId` maps items to epics), written by `factory pause|continue|stop` and the dashboard
  (`ControlActions`), read by every process. `RunPipeline` checks them before every step and, while a worker runs,
  polls them every second (`ControlWatch`): Pause → `IWorker.RequestPause` (a flag file under `<work root>/controls`
  that the worker's PreToolUse hook, passed by `--settings`, turns into deny + `continue: false`, ending the session at
  the next tool boundary), and the run is cancelled anyway after `Worker:PauseGraceSeconds`; Stop (item state
  `Stopping`) cancels at once. A pause-ended session's "success" result never counts as worker-done. Stop
  (`ItemStopper`): PRs back to draft (`prs-drafted`), board Stopped with a comment (`stop-reported`), Cancelled, item
  control cleared — each checkpointed, so a failed stop stays Stopping and is finished by the next run/poll. A paused
  factory makes the intake loop list no ready stories; the pipeline claims nothing new in a paused epic/factory.

- Every state change is a committed ledger row before the next step (`WorkLedger.RecordAsync`), checked
  against the transition table in `Ledger/Lifecycle.cs` first; an illegal transition throws and writes nothing.
  New phases add handlers in `RunPipeline.Handlers`, not table rows. Sub-steps inside a state are checkpoint
  rows (`LedgerEntry.Step`, `WorkLedger.CheckpointAsync`); `factory run` on an existing item resumes from
  its last row and skips recorded steps (the Claude session id is checkpointed as soon as it streams, so an
  interrupted Implement continues with `claude --resume`). Ctrl-C → Paused; a failure (including an
  unrequested `OperationCanceledException`, e.g. an HttpClient timeout) → Escalated + a story comment; a failed
  comment is retried on the next run before the item is re-queued.
- One run per item: `RunPipeline` holds a Postgres advisory lock on the item id (`PostgresRunLocks`) for the whole
  run, and `WorkItem.Version` is an optimistic concurrency token, so a stale writer's save throws.
- Workers lead their own process group (launched via `/usr/bin/perl` `setpgrp` + `exec`); the pid is checkpointed
  (`worker-started`) as soon as the process exists, and a resumed Implement stops a still-running orphan's group
  (`ClaudeWorker.StopOrphanAsync`, only if it is still a group leader running `Worker:ClaudePath`) before going on.
  Sandboxed, the recorded pid is sudo's and the owner can't signal `_factory`, so `StopOrphanAsync` instead runs a
  no-op through the helper, whose exit kills every `_factory` process (`WorkerSandbox.StopAllAsync`).
- Workers get only the router URL + router key; the worker env is an allowlist (`ClaudeWorker.BuildRouterVariables`,
  enforced again by `scripts/factory-worker-launch`).
- The orchestrator pushes only to `factory/*`, with a repo-scoped GitHub App installation token passed
  via git env config (never argv/remote URLs/.git/config) on every network git call. Nothing merges.
  Tokens are minted fresh per call (never cached) and refused if they outlive 1 hour (`GitHubApp`).
  Server-side, `github-repo protect` (`RepoProtection`) applies rulesets: default branch needs a PR, and only
  repo admins may write refs outside `factory/**` (needs GitHub Pro for private personal repos).
  That `~ALL` ruleset also stops non-admin integrations (Dependabot, `GITHUB_TOKEN` Actions deploys such as
  `gh-pages`) from writing any branch outside `factory/**`.
  Workers get no git/gh tools and run as `_factory`, which cannot reach the owner's keychain (App key) or
  gh/git credentials. Owner-side git on a worktree always passes
  `--git-dir`/`--work-tree`, with the admin dir found from the clone's side (never the worker-writable `.git`
  file, also on resume); worktrees live under the owner-owned work root (root-owned parent), are shared with
  `_factory` by an inheritable ACL set on the empty directory before checkout (never `chmod -R`, which follows
  symlinks), and are deleted (as `_factory` first) once the PR is open or the item escalates. Only a paused or
  crashed Implement keeps its worktree for resume; a worker that won't stop marks its exception
  (`WorkerStillRunning`) so nothing deletes under it. Each run sweeps worktrees no run will resume.
  The App private key never reaches a worker's env, argv or worktree (`ClaudeWorkerTests`).
- Every worker stdout line is a `session_events` row (E7): `ClaudeWorker` taps each line (`WorkerCallbacks.OnLine`) into a
  bounded channel that `SessionRecorder` drains into Postgres in order (gapless `Sequence`, non-JSON lines kept as `raw`);
  a resumed session continues its `worker_sessions` row and sequence. The row is named (`SetClaudeSessionIdAsync`) before
  the ledger checkpoints the session id, and a resume whose id matches no row continues the item's newest unnamed row.
  At session end the row gets exit status and the router cost (`GET /v1/sessions/:id/cost`, retried ~60 s while missing
  or zero; reporting only, E9). The recorder only stores: an insert trigger NOTIFYs `session_events`, and in `factory work`
  `SessionEventRelay` LISTENs (reconnecting and catching up from the ledger) and `SessionBroadcaster` pushes to
  `SessionHub` (`/hubs/sessions`, `JoinSession(id)`) viewers the stored backlog then live events, once each, whichever
  process (`factory work` or a separate `factory run`) recorded them. Slow viewers are disconnected, never waited on.
  The hub answers only `Host: 127.0.0.1|localhost` (plus the configured bind address/host name), same-origin (or
  Origin-less) clients, and logged-in ones.
- Dashboard (E8): Kestrel listens on 127.0.0.1 and at most one validated private address (`DashboardBinding`; never a
  wildcard). A fallback authorization policy makes every endpoint (pages, `/_blazor`, `/hubs/sessions`, static assets)
  require the cookie login except `/login` and the login form post (antiforgery + 5/min/IP rate limit). The dashboard
  reads the ledger (`IDashboardData`); its only writes are login/logout and the control form post (`/controls`,
  `DashboardControls`: login + antiforgery). Pipeline rows refresh on `NOTIFY work_items` (triggers on
  `work_items`/`worker_sessions`/`controls`, relayed by `SessionEventRelay` → `PipelineChanges`); session pages join the same
  `SessionBroadcaster` as hub viewers (`ISessionViewers`), so backlog-then-live holds there too. Never log transcript content.
- Processes migrate the ledger through `LedgerMigrations.MigrateAsync` (advisory-locked: EF alone lets two concurrent
  migrators apply the same migration) before reading it; tests migrate their temp database before starting a host.
- Tests: xunit.v3 on Microsoft.Testing.Platform (`global.json` opts in).
