# dark-factory

Autonomous software factory. A deterministic .NET orchestrator (no model decides a
state transition) moves Shortcut stories through a ledgered state machine and runs
Claude Code headless workers through the Weave router.

## Layout

- `src/DarkFactory.Orchestrator` — the `factory` CLI (System.CommandLine), EF Core ledger
  (`Ledger/`, migrations in `Ledger/Migrations`), Shortcut client, GitHub App auth and
  manifest setup (`GitHub/`), git worktrees (`Git/`), Claude worker (`Worker/`), router client.
- `tests/DarkFactory.Orchestrator.Tests` — unit tests (no network; fake HTTP APIs, InMemory EF, local git).
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
| `Factory:WorkRoot` | `~/.dark-factory` (clones + worktrees) |
| `ConnectionStrings:Ledger` | `Host=localhost;Port=5434;Database=factory;Username=factory;Password=factory` |
| `Worker:ClaudePath` | `claude` |
| `Worker:Auth` | `claude-login` (worker's own Claude login, router passes it through) or `router-key` (router key as Claude's API key; router needs BYOK provider keys) |
| `Worker:TimeoutMinutes` | `30` |

> **Until worker user isolation (S4, sc-25175) lands, workers run as the owner's macOS user.**
> The `dotnet build`/`dotnet test` tools run worker-written code with the owner's HOME, so it can reach
> the `dark-factory` keychain items and the owner's gh/git credentials. Only run `factory run` against the
> sandbox repo with trusted stories.

## Invariants (epic E1–E4)

- Workers get only the router URL + router key; the worker env is an allowlist (`ClaudeWorker.BuildEnvironment`).
- Every state change is a committed ledger row before the next step (`WorkLedger.RecordAsync`).
- The orchestrator pushes only to `factory/*`, with a repo-scoped GitHub App installation token passed
  via git env config (never argv/remote URLs/.git/config) on every network git call. Nothing merges.
  Tokens are minted fresh per call (never cached) and refused if they outlive 1 hour (`GitHubApp`).
  Server-side, `github-repo protect` (`RepoProtection`) applies rulesets: default branch needs a PR, and only
  repo admins may write refs outside `factory/**` (needs GitHub Pro for private personal repos).
  Workers get no git/gh tools, but see the S4 caveat above: they are not yet sandboxed from the owner's credentials.
- Tests: xunit.v3 on Microsoft.Testing.Platform (`global.json` opts in).
