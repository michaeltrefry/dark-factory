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
  and its Shortcut adapter (`Shortcut/ShortcutWorkSource.cs`), the Pause/Continue/Stop controls (`Controls/`), and the
  review and merge gate (`Gate/`: `ReviewModels`/`ReviewerChoice`, `ReviewPanel` (roles, risky paths, prompts, findings),
  `RouterReviewer`, `GatePolicy`, `MergeGate`, the new-tests check `NewTestsCheck`/`XunitNewTests`/`SandboxTestRunner`;
  reviewer prompts in `factory/prompts/`; GitHub side
  `GitHub/GateGitHub.cs`; pipeline handlers `RunPipeline.Gate.cs`).
- `scripts/` — `setup-worker-user.sh` (one-time root setup of the `_factory` sandbox user) and
  `factory-worker-launch` (the root-installed helper every sandboxed worker runs through).
- `tests/DarkFactory.Orchestrator.Tests` — unit tests (no network; fake HTTP APIs, InMemory EF, local git).
  `CrashResumeTests` also needs the compose Postgres (skips locally without it, fails under `CI`): it
  SIGKILLs `tests/DarkFactory.CrashHost` (the real pipeline with a fake `claude` script) and restarts it.
  `ShortcutContractTests` replay recorded Shortcut API fixtures (`Fixtures/shortcut`, strict request matching);
  re-record with `SHORTCUT_RECORD=1` (writes only throwaway `[dark-factory fixture]` stories, archived after).
- `tests/DarkFactory.AcceptanceTests` — live epic acceptance harness; skips unless `FACTORY_E2E=1`. AT1–AT8 runbook:
  `docs/acceptance.md` (the `factory work` tests use a throwaway ledger DB and their own work root, `E2e.cs`).

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
dotnet run --project src/DarkFactory.Orchestrator -- github-app setup --gate   # the merge gate's own App (then re-run protect)
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
| `GitHub:Gate:AppId`, `GitHub:Gate:PrivateKeyPem` | keychain `github-gate-app-id`, `github-gate-app-private-key` (written by `factory github-app setup --gate`): the merge gate's App, the only credential that merges |
| `Review:Models` | none — must be set: the router catalog (`GET /v1/router/models`, 2026-10-08) has no Claude Opus 5.5 or newer, and the factory refuses to start without one (`ReviewConfigurationException`: `factory run` and `factory work` print the reason and exit 2, before any router call) (every panel role's reviewer models in order, unless the role sets its own; each must be a Claude Opus 5.5 or newer, `ReviewModels.MeetsReviewFloor`: `claude-opus-<major>[-.]<minor>[-yyyymmdd]`, where the dotted and dashed spellings are one model everywhere ids are compared — pinned, served, reviewer vs second model — an older Opus, another Claude or another vendor's model is refused; the first entry reviews, whatever models the implementer used) |
| `Review:Correctness:Models`, `Review:SpecConformance:Models`, `Review:Security:Models` | `Review:Models` (one panel role's own reviewer models, in order; the same Claude Opus 5.5 or newer rule) |
| `Review:Confirm:Models` | `claude-opus-5,claude-sonnet-5` (the router catalog's Claude models; second models that confirm a blocking finding: the first Claude model that is not the reviewer's model; a non-Claude entry is refused) |
| `Review:TimeoutMinutes` | `10` (one reviewer call) |
| `Gate:CiPollSeconds`, `Gate:CiTimeoutMinutes` | `30`, `30` (CI on the PR head is polled until it finishes; still running at the timeout escalates) |
| `Gate:TestTimeoutMinutes` | `20` (one sandboxed run — restore, build, the new tests — of the `new-tests-fail-on-base` check; still running at the timeout fails the check) |
| `Factory:DefaultRepo` | `michaeltrefry/dark-factory-sandbox` (a story line `Repo: owner/name` overrides) |
| `Shortcut:Watch:Teams`, `Shortcut:Watch:Epics` | empty = watch nothing; comma-separated team mention names/ids, epic ids |
| `Intake:PollSeconds` | `60` |
| `Intake:MaxItemFailures` | `3` (runs of one item failing in a row before `factory work` escalates/parks it, E10) |
| `Usage:PollSeconds` | `60` (`factory work` reads the router's subscription usage; also read at each intake poll) |
| `Router:CostSettleSeconds` | `5` (after a session's cost is first recorded, it is read once more this much later and the later value kept) |
| `Factory:WorkRoot` | `/opt/dark-factory/work` (clones + worktrees; `~/.dark-factory` when `Worker:RunAs=none`) |
| `ConnectionStrings:Ledger` | `Host=localhost;Port=5434;Database=factory;Username=factory;Password=factory` |
| `Worker:ClaudePath` | `claude` (as the worker user sees it: `~_factory/.local/bin` is first on its PATH) |
| `Worker:RunAs` | `_factory` (sandbox user); `none` runs workers as the owner (development only) |
| `Worker:LaunchHelper` | `/usr/local/libexec/dark-factory/factory-worker-launch` |
| `Worker:Auth` | `router-key` (default: the worker holds only the router key, in `X-Weave-Router-Key` and as `ANTHROPIC_AUTH_TOKEN`; the router serves it from plans enrolled with `router login claude` / `router login codex`; `factory run`/`work` refuse to start unless `GET /v1/subscriptions/usage` lists an enabled `managed`/`shared` credential) or `claude-login` (weaker, violates E5: the worker's own Claude login, passed through by the router) |
| `Worker:TimeoutMinutes` | `30` |
| `Worker:PauseGraceSeconds` | `660` (a paused worker that has not stopped at a tool boundary by then is stopped; must exceed the 600 s longest tool call) |
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
`sudo scripts/setup-worker-user.sh` once (re-run it after a helper change: the installed helper's allowlist is
`ANTHROPIC_BASE_URL`, `ANTHROPIC_CUSTOM_HEADERS`, `ANTHROPIC_AUTH_TOKEN`; `EnsureReadyAsync` probes with the real
variable names, so a stale helper fails start-up naming the re-run). Every normal (re)run kills every `_factory`
process (its toolchain check runs through the helper): close `_factory` sessions and stop `factory work` first.
`--remove-worker-login` (opt-in) is a standalone mode: it runs none of the setup (no helper, no toolchain check, no
installs) and only deletes `_factory`'s own Claude login (`~/.claude/.credentials.json`, keychain item
`Claude Code-credentials`), as `_factory`, signalling nothing. The live probe tests (`LiveWorkerSandboxTests`) run only with
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
  are a `linked` checkpoint before Review. `factory work`'s start-up scope check is `IWorkSource.ValidateScopeAsync`. `ClaimAsync` refuses (no write) unless the fresh story is To Do or already
  ours, in scope (skipped by `factory run --ignore-scope`) and not another owner's, and reads the claim back; a refusal
  parks the item (Paused + `parked` checkpoint). Only Paused rows with detail `interrupted` (`RunPipeline.Interrupted`),
  `user-paused` (`RunPipeline.UserPaused`, a Pause control) or `usage-paused` (`RunPipeline.UsagePaused`, the usage pause)
  auto-resume (`RunPipeline.InFlightAsync`), the latter two only once no control pauses the item (the usage pause lifts at
  its `ResumeAt`); a parked item never does. Resumes re-check the scope.
- Controls (`Controls/`): Pause/Continue/Stop rows in the ledger's `controls` table (scope `factory` | `epic:<id>` |
  `item:sc-<id>`; `WorkItem.EpicId` maps items to epics), written by `factory pause|continue|stop` and the dashboard
  (`ControlActions`), read by every process. `RunPipeline` checks them before every step and, while a worker runs,
  polls them every second (`ControlWatch`): Pause → `IWorker.RequestPause` (a flag file under `<work root>/controls`
  that the worker's PreToolUse hook, passed by `--settings`, turns into deny + `continue: false`, ending the session at
  the next tool boundary), and the run is cancelled anyway after `Worker:PauseGraceSeconds`; Stop (item state
  `Stopping`) cancels at once. Continue before the boundary withdraws the flag (`IWorker.CancelPause`) and the grace.
  A pause-ended session (result `terminal_reason: hook_stopped`) never counts as worker-done; a worker that finishes
  on its own after a pause request does (worker-done recorded, then the pause takes effect before the push). Stop is
  re-checked before the PR is opened and after the last step, so the run that sees it finishes it. Stop
  (`ItemStopper`): PRs back to draft (`prs-drafted`), board Stopped with a comment (`stop-reported`), Cancelled, item
  control cleared — each checkpointed, so a failed stop stays Stopping and is finished by the next run/poll; an idle
  item whose worker a crashed run left is not finished by the CLI/dashboard but by its next run (which stops the
  worker first). Epic-scope Pause/Stop look up items with no `EpicId` on the board and record it (under the run lock);
  unresolvable ones are reported and the CLI exits non-zero (as on a refused Continue or an unfinished stop). A paused
  factory makes the intake loop list no ready stories; the pipeline claims nothing new in a paused epic/factory.
  Residual risk: the worker loads the target repo's `.claude/settings.json` (`--setting-sources project,local`), which
  can set `disableAllHooks` and so switch off the pause hook; the pause grace still stops such a worker (session and
  worktree kept), only not at a tool boundary.
- Usage pause (`Controls/Controls.cs` `UsagePause`, `Router/UsageMonitor.cs`): a factory-wide `usage` control row (reason,
  `ResumeAt`, `Backoff`) apart from the user's `factory` row, so neither's Continue/resume lifts the other; it pauses only
  while `now < ResumeAt` (`Control.PausesAt`), so it lifts by itself and the intake loop wakes at `ResumeAt`. `UsageMonitor`
  pauses when the router reports `all_exhausted` (until `resumes_at`, or a 1→60 min doubling backoff when none); unknown
  usage never pauses. Backstop: a worker result that is `WorkerResult.UsageLimited` (Claude Code's `"error":"rate_limit"`, or
  a marker such as the router's 429 "All enrolled subscription accounts are currently unavailable.") pauses with the
  backoff and records Paused `usage-paused` (auto-resumed, same session) instead of Escalated; `UsageLimited` matches the
  result text only when the result is an error (`is_error`), so model prose about rate limits still escalates. Only
  `continue --usage` lifts it early; Pause/Stop on it are refused. The lift holds against the reading that set it (a
  user-written `Running` row with `ResumeAt` ≥ the reported reset is not re-paused); a later reset, or the worker
  backstop, pauses again. The row carries Postgres `xmin` as a concurrency token (`Control.Version`): a losing write
  re-reads and decides again, so a backoff never shortens a known reset. A run stopped while a user's factory/epic/item
  Pause also holds is recorded `user-paused`, not `usage-paused`. Each usage read is bounded by
  `UsageOptions.RequestTimeout` (10 s), since the intake loop runs it inline.
  Owner decision (2026-10-08): when every subscription is exhausted the router may keep serving on a local model
  (`subscription_fallback`), but the factory still pauses on the router's `all_exhausted` — unattended work on the
  local model alone is not wanted.
  Deliberate scope decision: a 529/overloaded failure (`API Error: 529`, `overloaded_error`, "Repeated 529") counts as
  usage-limited — it pauses the factory with the backoff and never escalates the item — even though it is upstream
  capacity rather than the plans running out; a transient overload thus costs at most a backoff, not an escalation.

- Intake failures (E10, `WorkSources/IntakeLoop.cs`, `IntakeStatus`): a run that throws before its pipeline's try block
  counts against its item; after `Intake:MaxItemFailures` in a row `IItemRunner.GiveUpAsync` (`RunPipeline.GiveUpAsync`)
  escalates it (Intake/Implement, escalation comment) or parks it (Paused, with a comment) — an unknown item only gets the
  comment. Factory-wide failures (`FactoryUnavailableException` from `FactoryRunner`'s shared set-up: worker lock, sandbox,
  migration, worktree sweep; a `DbException`; `MissingCredentialException`; a failed poll) end the poll and escalate
  nothing. Both show on the pipeline page (`IntakeStatus`, in memory in the `factory work` process).
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
  via git env config (never argv/remote URLs/.git/config) on every network git call. Only the merge gate merges (below).
  Tokens are minted fresh per call (never cached) and refused if they outlive 1 hour (`GitHubApp`).
  Server-side, `github-repo protect` (`RepoProtection`) applies rulesets: default branch needs a PR, and only
  repo admins may write refs outside `factory/**` (needs GitHub Pro for private personal repos).
  With the gate App's id known (`github-app setup --gate`), that ruleset also lists the gate App as a bypass actor in
  `pull_request` mode only: it may merge a PR past it, never push.
  That `~ALL` ruleset also stops non-admin integrations (Dependabot, `GITHUB_TOKEN` Actions deploys such as
  `gh-pages`) from writing any branch outside `factory/**`.
  Workers get no git/gh tools and run as `_factory`, which cannot reach the owner's keychain (App key) or
  gh/git credentials. Owner-side git on a worktree always passes
  `--git-dir`/`--work-tree`, with the admin dir found from the clone's side (never the worker-writable `.git`
  file, also on resume); worktrees live under the owner-owned work root (root-owned parent), are shared with
  `_factory` by an inheritable ACL set on the empty directory before checkout (never `chmod -R`, which follows
  symlinks), and are deleted (as `_factory` first) once the PR is open or the item escalates. Only a paused or
  crashed Implement or fix round keeps its worktree for resume; a worker that won't stop marks its exception
  (`WorkerStillRunning`) so nothing deletes under it. Each run sweeps worktrees no run will resume.
  The App private key never reaches a worker's env, argv or worktree (`ClaudeWorkerTests`).
- Every worker stdout line is a `session_events` row (E7): `ClaudeWorker` taps each line (`WorkerCallbacks.OnLine`) into a
  bounded channel that `SessionRecorder` drains into Postgres in order (gapless `Sequence`, non-JSON lines kept as `raw`);
  a resumed session continues its `worker_sessions` row and sequence. The row is named (`SetClaudeSessionIdAsync`) before
  the ledger checkpoints the session id, and a resume whose id matches no row continues the item's newest unnamed row.
  At session end the row gets exit status and the router cost (`GET /v1/sessions/:id/cost`; reporting only, E9): retried
  ~60 s until recorded (200 with `request_count > 0`; a 404 or zero requests is not), then re-read once after
  `Router:CostSettleSeconds` and the later value kept (late-landing turns). A recorded cost of 0 is valid — the router
  prices turns served on its local model at $0 — and is stored as `CostUsd = 0` (dashboard `$0.00`; unknown is `—`). The recorder only stores: an insert trigger NOTIFYs `session_events`, and in `factory work`
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
- Review → CI → MergeGate → Merge (sc-25378; `RunPipeline.Gate.cs`, only in a pipeline built with a `GateStage`, which
  production always is): Implement records every model that answers the implementer (`implementer-model` checkpoints, from
  each assistant message's `model`; a record only — the panel does not depend on it). Review is a panel (sc-25379, `Gate/ReviewPanel.cs`): `correctness` and
  `spec-conformance` always, `security` only when the diff touches a path whose tier in the base branch's
  `factory/gate.yaml` lists `security-review` or that the code floor `RiskyPaths` matches (`GatePolicy.SecurityReviewReasons`,
  both recorded in the verdict's risky paths; see the floors under MergeGate below; a missing/invalid policy escalates at
  Review before any call). Each role reviews with the first entry of its own
  model list (`Review:<Role>:Models`, else `Review:Models`) that is a Claude Opus 5.5 or newer (owner decision 2026-10-08:
  every reviewer and second model is Claude, whichever models the implementer used — there is no cross-family rule;
  `ReviewModels`/`ReviewerChoice`; a role with no such model escalates before any call). Each role makes one
  router call (`RouterReviewer`: `POST /v1/messages`, router key only, `x-weave-force-model` pin, its own
  `X-Claude-Code-Session-Id` so the pin and the cost stay scoped to it) whose system prompt is the role's prompt file and
  whose message holds the story, the base commit's file list (`IGateGitHub.GetFilesAsync`) and the diff of the PR's head
  commit. Prompts: `factory/prompts/{correctness,spec-conformance,security,confirm}.md` in this repo, compiled in as
  embedded resources (`ReviewPrompts`) — never read from the target repo or the PR (which could rewrite the prompt it is
  judged by) nor from disk at run time; changing one is a dark-factory PR. Each role answers findings tagged `blocking` or
  `optional` (an unknown severity counts as blocking); each blocking finding goes to a second model (`Review:Confirm:Models`:
  the first Claude model that is not the reviewer's pinned or served model; none → escalate) with `confirm.md`: not confirmed → downgraded to optional (`downgraded: true`), does not
  block; confirmed, or an unusable confirm answer → stays blocking. Every panel call's session id is generated by the
  pipeline and checkpointed (`review-session`: "session model sha role prompt-path@sha256:hash"; role `confirm-<role>` for
  a second model) before the call, so a call that never returns still has a readable cost (E9) and the prompt it used is on
  record; it never goes in a row's `ClaudeSessionId`, which stays the implementer's (outcomes, escalation comments and
  stops read the last one). A Pause/Stop is checked before each panel call. The verdict (`ReviewPanel.Decide`,
  deterministic: pass only when every required role answered cleanly and no finding is blocking after confirmation; it
  lists the risky paths and every role's model, served model, session, prompt and findings) is a `verdict`
  checkpoint bound to that head SHA. Anything but a clean findings line from the pinned model is an unusable review and a
  fail: the router must name the served model and it must be the pinned id or its dated snapshot (`<pinned>-yyyymmdd`,
  `ReviewModels.Serves`) — a router answer naming no served model, or another model, included. Fix loop (sc-25380, `Gate/FixLoop.cs`, `FixAsync`): a fail whose
  only cause is blocking findings every one of which a second model confirmed (`FixLoop.Fixable`) goes Review → Fixing (row
  Detail = the fixed head); any other fail (an unusable answer, a missing role, an unusable confirmation) escalates. A fix
  round runs a fixer worker exactly like the implementer (same sandbox, router key only, session checkpointed and resumed,
  its models recorded as `implementer-model`) in a worktree restored from the PR branch,
  prompted with the story and only the confirmed blocking findings (fenced); its work is pushed to the same `factory/*`
  branch (`pushed` Detail = the pushed commit, `IRepoWorkspace.HeadAsync`) → Review. That review waits for the PR to show
  exactly that push (any other head — someone else's push — escalates), re-runs only the roles with an open blocking finding
  (plus a required role the fixed head's verdict lacks, e.g.
  security when the fix touches a path whose tier requires it, or one whose models break the panel's rule, `ReviewModels.Problems`); the
  others' reviews are carried into the new head's verdict marked `carried: <sha>` (the gate's merge reason names them). It
  then checkpoints `fix-progress` (`FixProgress`): progress only if the blocking count went down and no check run/status
  (by name, from GitHub's executed results) that passed on the fixed head — or reached no verdict there (`unknown_before`:
  cancelled, stale, no conclusion; compared like a passed one) — fails or has no run on the new head. It first waits for
  the fixed head's CI to finish, then for each such check on the new head (one missing once the new head's CI has
  finished is a regression); still unfinished after `Gate:CiTimeoutMinutes` escalates; either commit's CI not read in full
  is a failed round. Otherwise a failed round. Every round counts against
  `Lifecycle.MaxFixRounds` (3): a fail that would need a fourth escalates with the open findings listed in the comment.
  The router refusing a call for usage (429/529 or its
  exhaustion/rate-limit body: `RouterUsageLimitedException`) pauses the factory for usage (`reviewer-rate-limited`) like a
  worker's exhaustion; the item resumes and the head is reviewed again once it lifts. CI polls the head's check runs,
  commit statuses and check suites until finished (none at all keeps waiting until `Gate:CiTimeoutMinutes`); red
  escalates. A queued/in-progress check suite (a workflow registered but without check runs yet) keeps CI pending; a suite
  of a non-Actions App with no check runs is ignored (GitHub creates one per App with checks access; the Claude App's stays
  queued forever). A workflow GitHub has not registered at all yet is still invisible. MergeGate (`MergeGate.Evaluate`, E1–E3) is deterministic code over
  facts read fresh each time: `factory/gate.yaml` at the PR's **base** commit (`pull.BaseSha`, the commit the diff is read
  against; `GatePolicy`, `version: 2`, sc-25381: path tiers `sealed`/`protected`/`free` with `paths` and `checks`, `normal`
  with `checks` only, and `risk: {max_changed_lines, max_changed_files, max_fix_rounds}`; every key required; version 1 is
  rejected; missing/invalid/unreadable → no merge, escalate; no bypass key exists). The policy can tighten the gate but not
  loosen it below floors in code (E2; described once on `GatePolicy`): checks — every tier lists `ci-green` and
  `review-pass`, sealed/protected also `security-review`, protected/normal also `risk-threshold` and
  `new-tests-fail-on-base` (`GatePolicy.FloorChecks`);
  sealed — the tier must match the policy and CODEOWNERS (root, `.github/`, `docs/`) and cover `.github/workflows/` and
  `factory/prompts/` with a pattern matching everything under them (`PathPattern.CoversEverythingUnder`: a directory
  pattern or trailing `**` naming the directory or an ancestor; naming files in it is invalid); security review — a path
  whose tier lists `security-review` or that `RiskyPaths` matches (scripts, build/container files, dependency manifests,
  keys/.env, security-sensitive names; case-insensitive) needs it, so the policy can add security-review paths but not
  remove the floor. The gate also reads the PR, the diff of the head against the base (`IGateGitHub.GetDiffAsync`;
  unreadable → blocked; a file count — one per `diff --git` header, a rename once — other than the PR's `changed_files` →
  blocked as incomplete), the head's CI, the ledger's verdicts and fix rounds. Every path the diff touches (`DiffPaths`:
  both sides of a rename, deletes, C-quoted names unquoted, header-looking hunk lines ignored) is normalized (`RepoPath`:
  `./` and `..` resolved; one escaping the root, or an unparsed non-empty diff, is sealed) and put in the first of sealed >
  protected > free whose patterns (`PathPattern`: root-anchored, `*`/`?`/`**`, trailing `/` = directory; sealed and
  protected case-insensitive, free case-sensitive, so case only moves a path to a stricter tier) match, else normal; the change
  needs the union of its tiers' checks: `ci-green`, `review-pass` (the head's verdict passes, holds every required role, and
  every reviewer is a Claude Opus 5.5 or newer and every second model a Claude model other than its reviewer's, each
  served by the router as pinned — `ReviewModels.Problems`; the implementer's models are not consulted), `security-review` (the verdict has the
  security review), `risk-threshold` (changed lines, files — renames count both paths — and fix rounds within `risk`),
  `new-tests-fail-on-base` (sc-25382, below). A sealed path always escalates (even with no verdict); a protected one escalates after its checks instead of merging; each
  evaluation is a `gate` checkpoint. A head without a verdict (a push after the review) goes back
  to Review (CI/MergeGate → Review are table rows); otherwise any failed rule escalates. A pass checkpoints `gate-passed`
  with the head SHA and merges with the gate App's write token and `sha` = that head (GitHub refuses a moved head: 409 →
  Review); the Merge row's detail is the merge commit. A PR found merged — on resume after a crash or Ctrl-C during the
  merge call, or re-read after a merge call that failed (e.g. timed out) — is recorded as Merge if its head is one some
  `gate-passed` names (anywhere in the item's history), else escalates. Merge reports the board Merged (`merged-reported`), then
  Watch (no handler yet). Gate reads use a read-only gate-App token; only the merge mints a write one.
  `new-tests-fail-on-base` (sc-25382, `Gate/NewTests.cs`, run by `RunPipeline.NewTestsAsync` before `MergeGate.Evaluate`, only
  when the touched tiers require it and the head has a verdict): the tests the PR adds must fail on the base and pass on the
  head (a new test that already passes on the base checks nothing). Found deterministically on the gate's own clone
  (`IGateTestRunner`; `git diff --name-status -M base...head`, files read with `git cat-file`, never a symlink) by a
  per-stack `INewTestStrategy`: `XunitNewTests` (.NET/xUnit) takes every changed file under a `*.csproj` referencing an
  `xunit*` package as a test file and, by Roslyn syntax tree (nothing compiled), the `[Fact]`/`[Theory]` methods
  (`Namespace.Class.Method`, nested `+`) in their head versions that no base version of a changed test file has, each with
  its project (the deepest test project holding its file). Runs, each in a fresh detached throwaway worktree
  (`GitWorkspace.PrepareCommitAsync`, `gate-sc-<id>-base|head|base-retry`, shared with `_factory` like any worktree, deleted
  after; swept like any other): the base commit with the PR's test files applied (`OverlayAsync`: checked out from the head,
  renamed/deleted ones removed; files outside the test projects — production code, helpers elsewhere, the solution — stay
  the base's), and the head. Each runs, per project holding a new test (no solution file, so a new test project runs on the
  base too), `dotnet restore`, then `dotnet build --no-restore` (an errors-only file log too), then `dotnet test --no-build`
  on just its new tests (`--filter-method` per test + `--report-xunit-trx` under Microsoft.Testing.Platform per
  `global.json`, else VSTest `--filter FullyQualifiedName=…` + `--logger trx`) through `SandboxTestRunner`: sandboxed exactly
  like workers (`WorkerSandbox.Start` as `_factory` through the launch helper, no variables at all; `Worker:RunAs=none` runs
  as the owner with a minimal env), bounded by `Gate:TestTimeoutMinutes`. Results and build logs go to a fresh random
  `.factory-test-results-<guid>` directory per run (a worktree that already has it fails the run), so no commit can plant
  them; they are read as regular files only (no links), DTDs prohibited. A base that does not build is explained from its
  error log (`XunitNewTests.ExplainBuildFailure`, Roslyn on the applied files as built): any error that is not a `CS` compiler
  error in an applied `.cs` file (production code, an MSBuild/NuGet error, a generated file, no file) makes the base no
  evidence (`error`); a compiler error inside a new test's own declaration, or the header of a type containing it, means that
  test cannot pass on the base (`not-built`, counts as failing); the other new tests get one retry of the base
  (`base-retry`) without every member and `using` that held an error (written through owner-side git before anything
  runs), whose results judge them; a test the retry cannot build either, or with an error outside every member (e.g. an
  assembly attribute: no retry), stays `unproven`. So a head csproj referencing a project the base lacks, or a test calling
  a new helper outside the test projects, makes exactly the tests that use them `not-built`, and no test that passes on the
  base hides behind another one's compile error. Judged per test method (`NewTestsCheck.Judge`): pass on the head = every
  case passed; on the base each new test must have a failing case or be `not-built`. The result (`NewTestsResult`: outcome
  `pass` | `rejected` (a new test passes on the base, is skipped there, or is `unproven` — each named — or does not pass on
  the head) | `no-tests` (the PR adds none: deliberately a failure where the check is required, i.e. any normal or protected
  path; a docs/tests-only change needs no new test) | `unsupported` (a test-looking file of a stack no strategy reads, a
  test method outside an xUnit project, or new data rows on an existing theory, which cannot be run apart from its old
  rows) | `error` (restore failed, timed out, no results, a new test with no result on a base that ran, a base build error
  that is not a compiler error in the PR's test files, a head that does not build, a git/sandbox failure, no runner)), with
  every new test's cases on both commits, the retry's status and what it left out, is a `new-tests` checkpoint before the
  gate uses it (E5); a recorded non-error result for the same base and head is reused, never re-run. Anything but `pass`
  blocks (`gate_rejected` for the typed outcome). Pause/Stop are watched while the runs execute (polled like a worker's
  watch): either cancels them (the runner stops the sandboxed commands) and records nothing, so Continue runs the check again.
  Upgrading from Phase 1: Review is now a handled state, so the first `factory work` picks up every item parked at Review:
  an open PR is reviewed by the panel like any other (Phase 1's missing `implementer-model` no longer matters, sc-25379),
  and one the owner merged or closed escalates once with a story comment ("merged outside the factory"). An item whose
  head already has a verdict recorded under the dropped cross-family rule (e.g. a `gpt-5.5` or `claude-opus-5` reviewer)
  is reviewed again once by the current panel (`MergeGate.Superseded`: its models break `ReviewModels.Problems` and it is
  the only verdict on that head — at Review it is not reused; at MergeGate the gate answers ReviewHead when those are its
  only reasons), then merges as usual; the current panel's own verdict is a second one on the head, so if its models still
  break the rule the gate escalates instead of reviewing again. See docs/acceptance.md.
- Processes migrate the ledger through `LedgerMigrations.MigrateAsync` (advisory-locked: EF alone lets two concurrent
  migrators apply the same migration) before reading it; tests migrate their temp database before starting a host.
- Tests: xunit.v3 on Microsoft.Testing.Platform (`global.json` opts in).
