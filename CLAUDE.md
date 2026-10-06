# dark-factory

Autonomous software factory. A deterministic .NET orchestrator (no model decides a
state transition) moves Shortcut stories through a ledgered state machine and runs
Claude Code headless workers through the Weave router.

## Layout

- `src/DarkFactory.Orchestrator` — the `factory` CLI (System.CommandLine), EF Core ledger
  (`Ledger/`, migrations in `Ledger/Migrations`), Shortcut client, GitHub App auth and
  manifest setup (`GitHub/`), git worktrees (`Git/`), Claude worker and its sandbox (`Worker/`), router client.
- `scripts/` — `setup-worker-user.sh` (one-time root setup of the `_factory` sandbox user) and
  `factory-worker-launch` (the root-installed helper every sandboxed worker runs through).
- `tests/DarkFactory.Orchestrator.Tests` — unit tests (no network; fake HTTP APIs, InMemory EF, local git).
  `CrashResumeTests` also needs the compose Postgres (skips locally without it, fails under `CI`): it
  SIGKILLs `tests/DarkFactory.CrashHost` (the real pipeline with a fake `claude` script) and restarts it.
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
| `Factory:WorkRoot` | `/opt/dark-factory/work` (clones + worktrees; `~/.dark-factory` when `Worker:RunAs=none`) |
| `ConnectionStrings:Ledger` | `Host=localhost;Port=5434;Database=factory;Username=factory;Password=factory` |
| `Worker:ClaudePath` | `claude` (as the worker user sees it: `~_factory/.local/bin` is first on its PATH) |
| `Worker:RunAs` | `_factory` (sandbox user); `none` runs workers as the owner (development only) |
| `Worker:LaunchHelper` | `/usr/local/libexec/dark-factory/factory-worker-launch` |
| `Worker:Auth` | `claude-login` (worker's own Claude login, router passes it through) or `router-key` (router key as Claude's API key; router needs BYOK provider keys) |
| `Worker:TimeoutMinutes` | `30` |

Worker sandbox (E5): workers run as the hidden `_factory` user via
`sudo -n -u _factory factory-worker-launch claude …` (one NOPASSWD sudoers rule for that helper only).
The owner's home is ACL-denied to `_factory`, so `~/.ssh`, `~/.config/gh` and the login keychain are
unreachable. Router variables go to the helper on stdin; the helper builds the worker env from scratch
(PATH, HOME from the password database, MSBuild node reuse/build server off, router vars), refuses any
other variable or a program that isn't an absolute path/plain name, and when the worker exits or its stdin
closes kills the tree, the process group and then every `_factory` process (`kill -1` as `_factory`; the
helper skips that step when not running as the sandbox user). So `_factory` is single-tenant: one sandboxed
`factory run` per machine (`WorkerLock`, `<work root>/.factory-run.lock`). Run `sudo scripts/setup-worker-user.sh`
once; the live probe tests skip until then.

Secrets in the keychain: `security add-generic-password -w` at its interactive prompt truncates at 128 chars
(Shortcut tokens are longer → 401). Copy the secret, then
`printf 'add-generic-password -U -s dark-factory -a <account> -w %s\n' "$(pbpaste)" | security -i`
(printf is a builtin: nothing in argv) and verify with
`[ "$(security find-generic-password -s dark-factory -a <account> -w)" = "$(pbpaste)" ] && echo stored-ok`.

## Invariants (epic E1–E4)

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
- Tests: xunit.v3 on Microsoft.Testing.Platform (`global.json` opts in).
