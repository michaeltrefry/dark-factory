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
  and its Shortcut adapter (`Shortcut/ShortcutWorkSource.cs`), GitHub issues as a second source (`Issues/`: the intake
  `IssueIntake` — poll, triage, route, approvals — the routing rule `IssueTriage.cs`, the triage worker `TriageWorker.cs`,
  `GitHubIssueWorkSource`; GitHub side `GitHub/GitHubIssues.cs`), the Pause/Continue/Stop controls (`Controls/`), and the
  review and merge gate (`Gate/`: `ReviewModels`/`ReviewerChoice`, `ReviewPanel` (roles, risky paths, prompts, findings),
  `RouterReviewer`, `GatePolicy`, `MergeGate`, the new-tests check `NewTestsCheck`/`XunitNewTests`/`SandboxTestRunner`;
  reviewer prompts in `factory/prompts/`; GitHub side
  `GitHub/GateGitHub.cs`; pipeline handlers `RunPipeline.Gate.cs`, the merge gate and queue `RunPipeline.MergeQueue.cs`),
  and the outbound gateway (`Gateway/`: `OutboundHttp` builds every GitHub, Shortcut and router `HttpClient` (and the
  acceptance tests' dashboard client); `GitRemoteWrites` holds every `git push`'s arguments and git's installation-token
  credentials; `GitRemoteReads` the github.com remote URL and every clone/fetch's arguments (sc-25391); `GitHubCli` is the only
  `gh` run).
- `src/DarkFactory.Analyzers` — the gateway lint (sc-25390), a Roslyn analyzer every build of the orchestrator and of
  `tests/DarkFactory.AcceptanceTests` runs (CI's `build-test` included), all errors: DF0001 an HTTP client/handler (made,
  subclassed, in `typeof` or an explicit type argument like `Activator.CreateInstance<HttpClient>()`), socket, web socket,
  `WebRequest`/`WebClient`, `IHttpClientFactory`/`AddHttpClient`, or a subprocess started by `Process.Start`/`ProcessStartInfo`
  that is a network program (`gh`/`curl`/`wget`/`nc`/`ssh`/…, `NetworkPrograms`), a shell/interpreter/launcher (`bash`, `env`,
  `python`, `perl`, `sudo`, …: `Launchers`) whose arguments or member's strings name one, a URL or a network library, or a
  program whose name is not a constant unless read from `KnownLocalPrograms` (the inventory of today's: the gate's `TestStep.Program`,
  `WorkerSandbox.SudoPath`, `WorkerSandbox.RunChecked(program)`, `FactoryOptions.ClaudePath`, `CrashRestartTests.DotnetHost`;
  add one there only for a local program), outside `Gateway/`; DF0002 a git remote write outside it (text whose subcommand, past git's global
  options like `-C <dir>`/`--git-dir=…`, is `push`/`send-pack`/`http-push`/`remote set-url`, at its start — so a lone `push`
  argument — or after a `git` word); DF0003 a model provider host or key (`api.anthropic.com`, `ANTHROPIC_API_KEY`, …)
  anywhere, the embedded manifests, prompts, scripts and Razor markup included; DF0004 GitHub's or Shortcut's API host, or a
  github.com git remote (a `.git` URL, an `@github.com` user, git's `http.https://github.com/` config), outside it; DF0005 a
  `SuppressMessage`/`UnconditionalSuppressMessage` whose constant check id starts `DF`, anywhere (Roslyn honours those even for
  NotConfigurable rules, so the attribute itself fails the build). Since such an attribute could hide its own DF0005, the
  `GatewaySuppressionGuard` source generator (same assembly) adds an unsuppressible `#error` (CS1029) for each, and for any DF id
  quoted in a `.razor` file. Razor components' generated code is linted like any source (generated code is analyzed). Strings are
  read as the compiler folds them (`"api.github" + ".com"`, consts, interpolations). Every rule is
  `NotConfigurable` (no `#pragma`, `NoWarn` or `.editorconfig` severity turns it off), and both projects fail the build on
  `-p:RunAnalyzers=false` (target `RequireGatewayLint`). The unit test projects (`DarkFactory.Orchestrator.Tests`,
  `DarkFactory.CrashHost`) are exempt: they make only fake `HttpClient`s over fake handlers and loopback servers.
  `GatewayLintTests` seed each violation, lint the current tree (orchestrator — its sources plus every source its build generated,
  the Razor components' included, read from `obj/<config>/<tfm>/generated`: the orchestrator sets `EmitCompilerGeneratedFiles` —
  and acceptance tests, suppressed diagnostics included), build a throwaway Razor library with `dotnet build` to prove a Razor
  component's client and a (self-hiding) suppression fail the real build, and fail on anything that would switch the lint off: a
  bare `#pragma warning disable` or one naming a DF rule, an attribute naming `Gateway` or a DF rule (in `.cs` and in the
  generated Razor sources), a `DiagnosticSuppressor`, and in project/MSBuild/analyzer-config/CI files and `.razor` files a DF rule
  id, `dotnet_analyzer_diagnostic.`, `category-Gateway`, `<RunAnalyzers>`/`<RunAnalyzersDuringBuild>`, `RunAnalyzers…=` or
  `<Analyzer Remove>`. Gate checks: a test that makes a check fail carries `[FailsGateCheck("…")]`; `GateCheckCoverageTests`
  fails unless every check in `GateCheckCoverage.Registry` (`GateChecks.All`, which must hold every `GateChecks` constant; the
  policy floors; the merge gate's fail-closed `precondition:policy`/`precondition:pr-open`/`precondition:diff-complete` and
  `sealed-escalation`) has a running (not skipped/explicit) test tagged with that check alone (a multi-check test does not count),
  and unless each such test, run by the coverage test (every data row) with its check disabled through `Gate/GateCheckSeam`
  (internal, per async flow, set only by `Disable`, which no production code calls and which reads no config/env/CLI — a test
  checks both: no runtime bypass, E2), fails an assertion while passing with every check on (an empty or unrelated tagged test is
  reported).
- `scripts/` — `setup-worker-user.sh` (one-time root setup of the `_factory` sandbox user) and
  `factory-worker-launch` (the root-installed helper every sandboxed worker runs through).
- `tests/DarkFactory.Orchestrator.Tests` — unit tests (no network; fake HTTP APIs, InMemory EF, local git).
  Tests run `scripts/factory-worker-launch` only through `SafeHelper` (a checked copy whose every signal goes through a
  seam that signals only the test's own processes, and whose uid-sweep listing names only the helper's tree and the test's
  registered pids; `SafeHelper.Source` is private); `SafeHelperTests` fails if a test
  reaches the real helper another way (a repo path, the installed path, `Worker:LaunchHelper`/`options.WorkerSandbox`).
  A test signals a process only through `OwnProcess` (pid + start time, recorded where the pid cannot have been reused;
  `KillIfStillRunning` signals only while the start time still matches); `OwnProcessTests` fails on any other kill in
  `tests/` (`Process.Kill`, a kill(2) import, a `kill`/`pkill`/`killall` in a script).
  `CrashResumeTests` also needs the compose Postgres (skips locally without it, fails under `CI`): it
  SIGKILLs `tests/DarkFactory.CrashHost` (the real pipeline with a fake `claude` script) and restarts it.
  `ShortcutContractTests` replay recorded Shortcut API fixtures (`Fixtures/shortcut`, strict request matching);
  re-record with `SHORTCUT_RECORD=1` (writes only throwaway `[dark-factory fixture]` stories, archived after).
- `tests/DarkFactory.AcceptanceTests` — live epic acceptance harness; skips unless `FACTORY_E2E=1`. AT1–AT8 runbook:
  `docs/acceptance.md` (the `factory work` tests use a throwaway ledger DB and their own work root, `E2e.cs`). Phase 2's epic
  AT2/AT4/AT5/AT6 (sc-25391): `ReviewGateTests` (typed outcome per row, merge queue, PR body and closeout equal to the ledger's),
  `GateNegativeTests` (sealed path, corrupt policy on a throwaway branch, a new test passing on the base, a 4th fix round; review
  panel replaced by `SeededReviewer`, seeding through `SandboxRepo`), `FreezeAndStuckTests` (freeze seeded in the throwaway
  ledger; a stub Claude CLI replaying the stuck/silent fixtures), `IssueTriageTests.A_triage_session_that_read_issue_text_is_tainted_and_cannot_push`.

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
dotnet run --project src/DarkFactory.Orchestrator -- continue --freeze        # clear the automatic freeze (also --usage)
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
| `Router:BaseUrl` | `http://localhost:8080` (an absolute http(s) URL; one naming a model provider's host or key — `src/DarkFactory.Analyzers/ProviderMarkers.txt`, the gateway lint's list, embedded in both the analyzer and the orchestrator, `Gateway/ProviderMarkers.cs` — is refused, P1-E1) |
| `Router:Key` | env `FACTORY_ROUTER_KEY`, or keychain account `router-key` |
| `Shortcut:ApiToken` | env `SHORTCUT_API_TOKEN`, or keychain account `shortcut-api-token` |
| `GitHub:AppId`, `GitHub:PrivateKeyPem` | keychain `github-app-id`, `github-app-private-key` (written by `factory github-app setup`) |
| `GitHub:Gate:AppId`, `GitHub:Gate:PrivateKeyPem` | keychain `github-gate-app-id`, `github-gate-app-private-key` (written by `factory github-app setup --gate`): the merge gate's App, the only credential that merges |
| `Review:Models` | `claude-opus-5` (`ReviewPanelModels.DefaultReviewers`; owner decision 2026-10-09: the router catalog has no Opus 5.5 id and its `model_mapping` serves `claude-opus-5` as `claude-opus-5-5`) (every panel role's reviewer models in order, unless the role sets its own; each must be a Claude Opus 5 or newer, `ReviewModels.MeetsReviewFloor`, else the factory refuses to start — `ReviewConfigurationException`: `factory run` and `factory work` print the reason and exit 2, before any router call; ids read as `claude-opus-<major>[-.]<minor>[-yyyymmdd]`, where the dotted and dashed spellings are one model everywhere ids are compared — pinned, served, reviewer vs second model — an older Opus, another Claude or another vendor's model is refused; the first entry reviews, whatever models the implementer used) |
| `Review:Correctness:Models`, `Review:SpecConformance:Models`, `Review:Security:Models` | `Review:Models` (one panel role's own reviewer models, in order; the same Claude Opus 5 or newer rule) |
| `Review:Confirm:Models` | `claude-opus-5,claude-sonnet-5` (the router catalog's Claude models; second models that confirm a blocking finding: the first Claude model not pinned to the reviewer's pinned id and, once the router named the reviewer's served model, not one it may serve as that model (`ReviewModels.Serves`); a non-Claude entry is refused) |
| `Review:TimeoutMinutes` | `10` (> 0: one reviewer call, its whole answer stream included) |
| `Gate:CiPollSeconds`, `Gate:CiTimeoutMinutes` | `30`, `30` (each > 0: CI on the PR head is polled until it finishes; still running at the timeout escalates) |
| `Gate:TestTimeoutMinutes` | `20` (> 0: one sandboxed run — restore, build, the new tests — of the `new-tests-fail-on-base` check; still running at the timeout fails the check) |
| `Factory:DefaultRepo` | `michaeltrefry/dark-factory-sandbox` (a story line `Repo: owner/name` overrides) |
| `Shortcut:Watch:Teams`, `Shortcut:Watch:Epics` | empty = watch nothing; comma-separated team mention names/ids, epic ids |
| `GitHub:Watch:Repos` | empty = triage no issues; comma-separated `owner/name` whose issues `factory work` polls (the workers' App needs Issues read/write there) |
| `Intake:PollSeconds` | `60` (> 0) |
| `Intake:MaxItemFailures` | `3` (runs of one item failing in a row before `factory work` escalates/parks it, E10) |
| `Usage:PollSeconds` | `60` (> 0: `factory work` reads the router's subscription usage; also read at each intake poll) |
| `Freeze:MaxConsecutiveFailures` | `3` (≥ 1: distinct items escalated in a row, no factory merge between, that freeze the factory) |
| `Freeze:HotFileMerges`, `Freeze:HotFileWindowHours` | `3` (≥ 2), `24` (> 0): factory merges changing one file within the window that freeze it |
| `Freeze:CostRisingRounds` | `2` (≥ 1): consecutive rises of an item's cost per round (implement, then each fix round) that freeze it |
| `Freeze:CheckFailedEvaluations`, `Freeze:CheckFailedMinutes` | `10` (≥ 1), `30` (> 0): a freeze check failing that many evaluations in a row (counted per process, `FreezeCheckFailures.Process`), or for that long, is written as a `check-failed` freeze |
| `Controls:MaxReadFailures` | `10` (≥ 1): failed reads in a row of an item's controls while its worker or the gate's test runs go on; the next counts as a Pause (`controls-unreadable`) |
| `Router:CostSettleSeconds` | `5` (after a session's cost is first recorded, it is read once more this much later and the later value kept) |
| `Factory:WorkRoot` | `/opt/dark-factory/work` (clones + worktrees; `~/.dark-factory` when `Worker:RunAs=none`) |
| `ConnectionStrings:Ledger` | `Host=localhost;Port=5434;Database=factory;Username=factory;Password=factory` |
| `Worker:ClaudePath` | `claude` (as the worker user sees it: `~_factory/.local/bin` is first on its PATH) |
| `Worker:RunAs` | `_factory` (sandbox user); `none` runs workers as the owner (development only) |
| `Worker:LaunchHelper` | `/usr/local/libexec/dark-factory/factory-worker-launch` |
| `Worker:Auth` | `router-key` (default: the worker holds only the router key, in `X-Weave-Router-Key` and as `ANTHROPIC_AUTH_TOKEN`; the router serves it from plans enrolled with `router login claude` / `router login codex`; `factory run`/`work` refuse to start unless `GET /v1/subscriptions/usage` lists an enabled `managed`/`shared` credential) or `claude-login` (weaker, violates E5: the worker's own Claude login, passed through by the router) |
| `Worker:TimeoutMinutes` | `30` (> 0) |
| `Worker:PauseGraceSeconds` | `660` (a paused worker that has not stopped at a tool boundary by then is stopped; must exceed the 600 s longest tool call) |
| `Worker:StuckRepeats` | `5` (≥ 2: near-identical turns, or cycles of up to 4 turns, in a row that make a running worker stuck; sc-25388) |
| `Worker:StuckSimilarity` | `0.96` (0 < s ≤ 1: trigram Dice similarity, after normalising whitespace (and digits outside tool inputs), at which two turns count as the same) |
| `Worker:QuietMinutes` | `10` (> 0: a running session with no event for this long is marked quiet on the dashboard; silence never interrupts) |
| `Factory:HostPort` | `47822` (0–65535, 0 = any free port; `factory work`: 127.0.0.1, plus `Dashboard:BindAddress`) |
| `Dashboard:BindAddress` | unset = loopback only; one private address (RFC 1918, 100.64/10, fc00::/7) on a local interface |
| `Dashboard:HostName` | extra Host header the dashboard answers to (e.g. MagicDNS name); one plain DNS name, no wildcard/port |
| `Dashboard:PasswordHash` | keychain `dashboard-password-hash` (written by `factory dashboard set-password`) |
| `Metrics:SandboxRepos` | `michaeltrefry/dark-factory-sandbox` (comma-separated `owner/name`; items on these repos are left out of every metric) |
| `Metrics:DemoMarkers` | `[demo],[sandbox]` (an item whose title carries one, case-insensitively, is left out of every metric) |

Every setting with a rule is checked at start-up (`FactoryOptions.ValidateSettings`, before any other check): `factory run` and
`factory work` print the key and why and exit 2 (E6: each key has a consumer and a test that a non-default value is read and an
out-of-range one refused, `FactoryOptionsTests`).

Worker sandbox (E5): workers run as the hidden `_factory` user via
`sudo -n -u _factory factory-worker-launch claude …` (one NOPASSWD sudoers rule for that helper only).
The owner's home is ACL-denied to `_factory`, so `~/.ssh`, `~/.config/gh` and the login keychain are
unreachable. Router variables go to the helper on stdin; the helper builds the worker env from scratch
(PATH, HOME from the password database, MSBuild node reuse/build server off, router vars), refuses any
other variable or a program that isn't an absolute path/plain name, and when the worker exits or its stdin
closes kills the tree, the process group and then every other `_factory` process (a `pgrep -U` sweep that
spares the helper's own pid and its descendants — never `kill -1`, which on macOS kills the sender too), then exits
with the worker's status. The sweep runs only when the helper's user name is `sandbox_user` and its uid is
`sandbox_uid`, which setup pins (it must be in 400-499, the range setup allocates, still `sandbox_user`'s uid, and not
the sudo caller's); otherwise the helper prints `refusing the _factory uid sweep: <why>` and skips it. `send_signal`
never signals pid or group 0 or 1 or an empty target. So `_factory` is
single-tenant: one sandboxed `factory run` per machine (`WorkerLock`, `<work root>/.factory-run.lock`). Run
`sudo scripts/setup-worker-user.sh` once, and again after any change to `scripts/factory-worker-launch`: it installs
the helper with `sandbox_user` and `sandbox_uid` set (setup refuses a worker uid outside 400-499). `EnsureReadyAsync`
reads the installed helper first and fails start-up with `Stale helper: … re-run \`sudo scripts/setup-worker-user.sh\``
unless it has `helper_version` ≥ `WorkerSandbox.HelperVersion` (2) and, with those two lines undone, the SHA-256 of the
repo's helper (compiled into the orchestrator as a resource); then it probes with the real router variable names
(allowlist `ANTHROPIC_BASE_URL`, `ANTHROPIC_CUSTOM_HEADERS`, `ANTHROPIC_AUTH_TOKEN`) and fails if the probe's stderr
has a uid-sweep refusal; last it runs `<Worker:ClaudePath> --version` as `_factory` through the helper and fails start-up with
`Upgrade _factory's claude: …` (and the `claude update` command) below `WorkerSandbox.MinClaudeVersion` (2.1.291, the release
the triage confinement — `--setting-sources ""`, `blockReadsOutsideWorkingDirectories`, `dontAsk` — was checked against; an
older CLI may silently ignore a setting it does not know). Every normal (re)run kills every `_factory`
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
  parks the item (Paused `claim refused: …` + `parked` checkpoint). A source that lists a refused item again by itself
  (`IWorkSource.MaxClaimRefusals`; GitHub issues: `GitHubIssueWorkSource.ClaimRefusals` = 3) bounds it: a released issue whose
  claim is refused is listed again on the next poll, each refusal recorded, and the refusal that reaches the bound escalates
  the item (Escalated + comment, E10); Shortcut (null) waits for the story to be ready again. Wording a human acts on comes
  from the source (`IWorkSource.ScopeReturnHint`, `ItemNaming.Noun`), never Shortcut terms on a GitHub issue. Only Paused rows with detail `interrupted` (`RunPipeline.Interrupted`),
  `user-paused` (`RunPipeline.UserPaused`, a Pause control), `usage-paused` (`RunPipeline.UsagePaused`, the usage pause),
  `freeze-paused` (`RunPipeline.FreezePaused`, the automatic freeze) or `controls-unreadable` (`RunPipeline.ControlsUnreadablePaused`)
  auto-resume (`RunPipeline.InFlightAsync`), all but the first only once no control pauses the item and the controls can be read
  (the usage pause lifts at its `ResumeAt`, the freeze at a human's Continue); a parked item never does. The same pauses keep a
  queued item's merge-queue place (`MergeQueue.Member`). `InFlightAsync` lists the items being stopped first. Resumes re-check
  the scope.
- Work sources are named by `ItemNaming` (`sc-<story>`, `gh-<key>`: the ledger `Source`, external id, `factory/<id>` branch,
  `factory-<id>` worktree, `item:<id>` control scope); `factory run`/`--item` take either. The intake loop polls every source
  in turn (`IntakeLane`), so a triage and an item run never share the sandbox. The merge queue is per repo across sources.
- GitHub issues (sc-25385, `Issues/`): `IssueIntake` polls each watched repo's open issues updated since its ledger cursor
  (`github_issue_cursors`; the first poll of a repo starts from now, no backfill). Each issue gets a key (`github_issues`) and a
  work item `gh-<key>` that waits Paused/parked (`awaiting triage`) until released. Each issue version (sha of title+body) is
  triaged once by a read-only, unpinned worker (`WorkerTriageRunner` over `WorkerTools.ReadOnly`: its only allow rule is
  `Read(//<abs triage worktree>/**)` (`WorkerTools.ReadRule`; Claude Code bounds Glob/Grep by Read rules), `--permission-mode
  dontAsk` (every call that would prompt — any read outside the working directory — is auto-denied), `--settings` with
  `permissions.blockReadsOutsideWorkingDirectories`, `--setting-sources ""` (no repo or worker-user settings file can widen
  it), every known write/exec/sub-agent/worktree/web tool in `WorkerTools.WriteOrExecTools` denied by name (belt and braces: not
  exhaustive as the CLI adds tools — the guarantee is `dontAsk` with no allow rule but the `ReadRule`, so any unnamed tool is
  auto-denied too); the runner refuses any worker whose tools
  are not `IsReadOnly`, so a tainted session cannot change a file an untainted session later
  pushes, E4; the prompt has it reason from the code, building and running nothing) in a throwaway `factory/triage-gh-<key>`
  worktree under its own root (`<WorkRoot>/triage-worktrees`, `SandboxTriageRunner.TriageWorktrees`: never the items'
  `worktrees`, swept before each triage, not ACL-shared for writing — the worker user reads it through the work root's
  inherited read entry) whose git holds a contents-read token; nothing is committed or pushed; the issue text is fenced as
  untrusted. After checkout and before the session starts, the owner removes every symlink in that worktree whose `realpath`
  is outside it (a dangling or looping one counts as outside; `TriageWorktree.RemoveOutOfTreeSymlinks`), logging each
  `[triage] gh-<key>: removed symlink <link> -> <target>`. `factory work` refuses to start with `GitHub:Watch:Repos` set and `Worker:RunAs=none`
  (`IssueIntake.UnsandboxedRefusal`; `SandboxTriageRunner` refuses an unsandboxed triage too, factory-wide). Its posted free
  text is bounded (`TriageParser`: title 120, summary and fix 1,500 chars each) and fenced. Residual: it runs as the same
  `_factory` user as the items' workers, but its file tools read only its own triage worktree (not other clones, kept
  worktrees or the worker home's transcripts); it can still restate what it read there — the target repo's own code at its
  default branch, the repo the issue is about — inside that bounded, fenced answer. Residual, accepted: Read and the read block
  are Claude Code's own enforcement, and on Glob and Grep (a search given an explicit path or pattern outside the working
  directory) it is best-effort and unverified by the factory; a gap there would reach what `_factory` can read. The intake reads and writes the issue board only through the issue source's
  `IIssueIntakeSource` capability (E6): triage comment and route label, issues, comments, permissions and gate policy. The orchestrator — not
  the model — routes (`IssueRouting`): question/duplicate → comment only; author without write/maintain/admin
  (`RepoPermission.IsCollaborator`; read and triage count as outsiders) on the issue's repo, or on the target repo when the
  triage names another watched repo → comment + `awaiting-approval`; collaborator with an
  apparent fix (confidence ≥ 0.8; a fix naming its paths, none sealed/protected under the target's
  `factory/gate.yaml` on its default branch; exactly one watched target repo) → `released` (built); else → comment +
  `needs-human` and a `routed-to-human` row (outcome escalated, so the escalation metric counts it). The model's `reproduced`
  is its own claim (the triage runs nothing; E5): it never routes and is shown only inside the comment's fence, labelled
  model-reported. The `triaged` row (route included) is written before anything is posted; comments carry a
  `<!-- dark-factory:triage <hash> -->` marker (trusted only on the App's own comments: `performed_via_github_app.id`, or
  failing that the App's bot account — `user.type` Bot and login `<slug>[bot]`, slug from `GET /app`), so a retried post is
  found, not repeated. Approval: a collaborator's comment whose trimmed text is exactly `Approved` (case-sensitive, never
  edited — an edit moves `updated_at`), bound to the last triage comment before it, releases a releasable triage when that is
  the current triage and the approver is also a collaborator on the target repo; every other `Approved` is recorded
  `approval-ignored` once. The released triage is the item's scope (later edits are
  not triaged) and its spec (`ReadSpecAsync`: the triage only, `Repo:` line first); the PR body says `Closes owner/name#N`,
  and Merged closes the issue if still open. The triage title is untrusted: commit message, PR title and body carry it through
  `UntrustedText.Inert` (no mention, reference, link, image or HTML; a code span in the body). All issue writes use
  issues-only tokens (`GitHubIssuesClient`). A triage that fails `Intake:MaxItemFailures` times is itself the triage
  (needs-human; its error cut at 2000 characters); one refused for usage pauses the factory. Listing pages by keyset (`since` =
  the last page's last `updated_at`). A request GitHub refuses for good (`GitHubRequestException.Permanent`: a 4xx that is not
  a rate limit, 401 or 408) is recorded `github-refused` on the item and the dashboard and does not hold the repo's cursor; the
  issue is taken up again when it next changes.
- Controls (`Controls/`): Pause/Continue/Stop rows in the ledger's `controls` table (scope `factory` | `epic:<id>` |
  `item:sc-<id>`; `WorkItem.EpicId` maps items to epics), written by `factory pause|continue|stop` and the dashboard
  (`ControlActions`), read by every process. `RunPipeline` checks them before every step and, while a worker runs,
  polls them every second (`ControlWatch`): Pause → `IWorker.RequestPause` (a flag file under `<work root>/controls`
  that the worker's PreToolUse hook, passed by `--settings`, turns into deny + `continue: false`, ending the session at
  the next tool boundary), and the run is cancelled anyway after `Worker:PauseGraceSeconds`; Stop (item state
  `Stopping`) cancels at once. Continue before the boundary withdraws the flag (`IWorker.CancelPause`) and the grace. Controls
  that cannot be read more than `Controls:MaxReadFailures` times in a row count as a Pause (E2: never run blind) — for a worker
  and for the gate's test runs (`TestRunWatch`) — recorded as a `controls-unreadable` checkpoint (the error; outcome failed) then
  Paused `controls-unreadable` (session and worktree kept); a read that then shows nothing pausing the item withdraws it.
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

- Automatic freeze (sc-25387, `Controls/Freeze.cs` `FactoryFreeze`): a factory-wide `freeze` control row (trigger in `Reason`,
  words in `Detail`), set only by the evaluator (`ChangedBy` `freeze`) and cleared only by a human's Continue
  (`factory continue --freeze`, the dashboard banner's Continue); Pause/Stop on it are refused. `RunPipeline.RunAsync` runs the
  evaluator before every dispatch (after finishing a pending Stop, before anything is read from the board or claimed): a freeze
  row that is not `Running` holds; otherwise the triggers are checked and the first that holds is written (before the dispatch is
  refused) — `consecutive-failures` (the trailing Escalated rows with no Merge row after them cover `Freeze:MaxConsecutiveFailures`
  distinct items), `hot-file` (one file of one repo in the `merge-files` checkpoints of `Freeze:HotFileMerges` factory merges
  within `Freeze:HotFileWindowHours`; the gate records the PR's base branch and diff files as `merge-files` before every merge),
  `cost-rising` (an active item's — not Merge/Watch/terminal — rounds — its last Implement, then each fix round — whose worker sessions' router costs rise
  strictly `Freeze:CostRisingRounds` times in a row; a round with no session or an unrecorded cost is no evidence), `main-red`
  (a state: per repo the factory works on now — the configured repos, `FactoryOptions.ConfiguredRepos` = `Factory:DefaultRepo` and
  `GitHub:Watch:Repos`, and the repos of non-terminal items — the base branch's head after its latest factory merge over all history
  is `Ci.Evaluate` Failed; pending is not red; a base the compare answers 404 for under a working installation
  (`GitHubNotFoundException`: the branch or merge commit is gone) has no tip, so nothing to be red; a gate App that is not
  installed on the repo or cannot see it (`GitHubAppNotInstalledException`: the installation lookup's or token mint's 404)
  fails the check instead (`freeze-check-failed`, held as `check-failed`) — a `FreezeStatus.Notes` line, logged `[freeze]`; a head a Continue
  acknowledged — the `red-tip <repo>@<base> <sha>` lines of the cleared freeze's `Detail`, carried into later freezes — is skipped
  until the head moves). The run then returns the typed outcome `deferred` (`RunOutcome.Deferred`, nothing about the item changes;
  the error says what lifts it, `FreezeTrigger.Remedy`); the intake loop runs no ready item of any lane after a deferral and stops a
  lane's in-flight items at its first deferral (their stops, listed first, still run in every lane), lists no ready items while the
  row is set, and shows the deferral (`IntakeStatus.DeferredRun`, not cleared by a later stop) and the freeze banner on the
  pipeline page. Each intake poll runs the evaluator too (`IntakeLoop` `freeze`, `FactoryRunner.CreateFreeze`) before any lane
  is prepared (triage) or listed: frozen or uncheckable, nothing is triaged or listed. Within a run the evaluator also runs
  before every step but Merge's bookkeeping, before every worker session starts (implement, fixer, CI fixer), before a merge-queue
  base-update push and right before a merge (`RunPipeline.ThrowIfFrozenAsync`); frozen there, the run pauses like a Pause
  (Paused `freeze-paused`, also when the check failed and wrote no row). Cost rule: the ledger triggers are read fresh every
  check; main-red's GitHub reads (base tip per merge, CI per tip) are reused per evaluator for `FactoryFreeze.MainRedCacheTtl`
  (30 s), except the check right before a merge, which reads them fresh. `continue --freeze` on a factory that is not frozen (no
  row, or `Running`) is refused ("the factory is not frozen") and writes nothing. Continue rule: each trigger but main-red counts only events recorded after the last Continue of the freeze (its row's
  `ChangedAt`), so a Continue is never re-frozen by the evidence it acknowledged, and a new occurrence after it freezes again;
  a freeze decided on a row a Continue has since changed is not written (`IControls.FreezeAsync` compares `ChangedAt`). An
  unreadable freeze record counts as frozen; a trigger that cannot be checked (ledger or GitHub unreadable) defers the dispatch
  too (E2: `freeze-check-failed`, which resumes by itself once checkable) but writes no freeze — until the failure persists
  (P1-E10: none sits silently): after `Freeze:CheckFailedEvaluations` failed evaluations in a row or `Freeze:CheckFailedMinutes`
  it is written as a `check-failed` freeze naming the failing check (`ledger` or `main-red <repo>@<base>`) and a
  `check-failed <check>: <exception type>` line; a human's Continue acknowledges exactly that failure (the same check failing the
  same way is passed over) until it changes; such lines are not carried into later freezes. A trigger that holds still wins over
  another repo's failing read. In-flight work: the row pauses every item through `EffectiveAsync` like a factory Pause — no
  new step starts, a running worker stops at its next tool boundary (Paused `freeze-paused`, session and worktree kept) and
  resumes automatically after the Continue.
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
- Typed step outcomes (E7, sc-25389): every ledger row has `Outcome` (`passed`, `failed`, `gate_rejected`, `deferred`,
  `escalated`; NOT NULL + a check constraint), decided in one place — `StepOutcomes.Of(from, state, step, detail)`, called by
  `WorkLedger` for every row it writes (`LedgerEntry.Outcome` is `required`). The rules map earlier stories' typed results (verdict,
  fix-progress, gate decision, new-tests outcome, stuck rounds, pauses, issue routes); migration `StepOutcomes` backfilled older rows
  with the rules as they stood, as frozen SQL (a Postgres test checks it against a frozen literal corpus; never edit the shipped
  migration — a later rule change only affects new rows). Reports are rendered only from the ledger (`LedgerReport`, E5): the
  PR description at open and again at merge (`pr-report`), the board closeout on merge (`closeout`) and the escalation comment list
  the check rows (`gate`, `ci-failure`, `new-tests`, `merge-conflict`) and verdict rows (each section's heading counts them by
  outcome), the fix rounds and the worker cost (N/A when unrecorded; the dashboard's pipeline cost cell likewise: N/A when no session
  is measured, `$x of n/m measured` when some are not, `Format.ItemCost`; a session's own cost N/A until measured); model/repo text in rows goes through `UntrustedText` (code span,
  or a tilde fence for the escalation reason). Reports are bounded under GitHub's 65,536-char limit (Shortcut's is higher):
  `Facts` ≤ 60,000 chars — ≤10 blocking findings per verdict, the latest verdict always in full, then the newest checks and earlier
  verdicts that fit, with an "N earlier … not shown" line; the escalation reason is clipped to 4,000 chars. A failed `pr-report` or
  `closeout` is recorded (`failed: …`, outcome failed), never escalated: the merge stands. A failed closeout shows on the dashboard
  item and is retried from Watch on the next polls (`InFlightAsync` lists it; `HoldAsync` posts it), each attempt recorded, up to
  `RunPipeline.MaxCloseoutAttempts` (3) per merge; after that the dashboard says to post it by hand. `LedgerMetrics` (dashboard
  pipeline page; computed at most once per 60 s, `DashboardData.DefaultMetricsTtl`, and a metrics failure marks only the metrics
  stale) computes cost per merged PR, 14-day revert rate (N/A: no revert
  data yet), escalation rate, fix rounds per merged PR, reviewer precision (blocking findings a second model confirmed / those it
  answered) and intake-to-merge time; N/A over zero matching items; sandbox/demo items are excluded (`Metrics:*`).
- One run per item: `RunPipeline` holds a Postgres advisory lock on the item id (`PostgresRunLocks`) for the whole
  run, and `WorkItem.Version` is an optimistic concurrency token, so a stale writer's save throws.
- Workers lead their own process group (launched via `/usr/bin/perl` `setpgrp` + `exec`); the pid is checkpointed
  (`worker-started`) as soon as the process exists, and a resumed Implement stops a still-running orphan's group
  (`ClaudeWorker.StopOrphanAsync`, only if it is still a group leader running `Worker:ClaudePath`) before going on.
  Sandboxed, the recorded pid is sudo's and the owner can't signal `_factory`, so `StopOrphanAsync` instead runs a
  no-op through the helper, whose exit kills every `_factory` process (`WorkerSandbox.StopAllAsync`).
- Workers get only the router URL + router key; the worker env is an allowlist (`ClaudeWorker.BuildRouterVariables`,
  enforced again by `scripts/factory-worker-launch`).
- Untrusted-input taint (E4, sc-25386, `Worker/Taint.cs`): a worker session that has read untrusted content is tainted — a
  `session_taints` row keyed by its Claude session id (insert-only; a Postgres trigger refuses UPDATE/DELETE; the first reason
  is kept), so it holds across resume and crash and nothing clears it. Sources: the input the orchestrator hands it
  (`Taint.Of(WorkerInput)`: issue text and outsider comments taint; the owner's story, an issue item's approved triage summary,
  review findings and CI logs do not), recorded before the session id is checkpointed (the triage worker: `issue-text`); and any
  `tool_use` of `WebFetch`/`WebSearch` or of an MCP tool in its stream-json (`web:<tool>`, `mcp:<tool>`; the use counts, even a
  denied one), recorded by `ClaudeWorker` (`WorkerCallbacks.OnUntrusted`) before the next line is read — with no callback to
  record it the run fails (E2). Workers are also denied the web tools (`--disallowedTools WebFetch WebSearch`, beating any allow
  rule in the target repo's settings). Every push of worker work needs a `PushGrant` (`IRepoWorkspace.CommitAndPushAsync`; no grant,
  no token minted), which only `WorkLedger.GrantPushAsync` issues, for the current attempt's sessions, none tainted (none at all is
  refused too); the merge queue's base-update fast-forward (`PushAsync`) pushes only the orchestrator's own merge commit. A tainted implementer/fixer session is refused before it is resumed and at its push: `SessionTaintedException`
  escalates the item (comment names the session and reason), its worktree is removed, and a re-run starts a fresh session.
  Deliberate: CI logs are not a taint source (they come from the repo's CI running the base plus the factory's own reviewed
  changes; tainting them would leave no CI fixer able to push).
  Prompt fences: every prompt puts text written by others in a `<tag>` block via `Worker/PromptFence` (`Block`/`Escape`: the
  block's own closing tag, any case or spacing, is neutralised inside it) — the issue (`issue-title`, `issue-body`), the
  reviewer context (`story`, `files`, `diff`, `finding`), findings, CI logs (`ci-log`), conflicted paths (`file`). A GitHub issue
  item's spec (the approved triage's title, summary and proposed fix: model text from untrusted issue text) is fenced as data
  (`triage`, `PromptFence.Spec`) in every worker prompt (implement, review fix, CI fix, conflict fix); a Shortcut story is
  written on the owner's board and stays unfenced instructions.
  Interrupts: the taint write of a tool use runs on a never-cancelled token (`ClaudeWorker` passes `CancellationToken.None` to
  `OnUntrusted`), and before resuming a session `RunWorkerSessionAsync` replays its stored `session_events` through
  `StreamJsonState` (`WorkLedger.ReplayTaintsAsync`) and taints it for every web/MCP use found; a failed read fails the run.
  Repo settings (`Taint.OfRepoSettings`, reason `repo-settings`): before launch, and again after the worker's run (it may write
  them itself), the worktree's `.claude/settings.json` and `.claude/settings.local.json` (the `project,local` sources) are read;
  `hooks`, `enableAllProjectMcpServers`, `enabledMcpjsonServers`, a command helper (`apiKeyHelper`, `awsAuthRefresh`,
  `awsCredentialExport`, `otelHeadersHelper`, `statusLine`), any `permissions.allow` rule not in `ClaudeWorker.AllowedTools`, or a
  `.mcp.json` naming a server taints the session (a new one from its start; a resumed one is refused). Unreadable or unparseable
  settings taint too. Residuals: an orchestrator SIGKILLed after the worker wrote a tool_use line but before the line was stored
  (pipe buffer / capture queue) leaves nothing to replay. Accepted: the allowed `dotnet build/test/restore` run repo and package
  code (MSBuild targets, analyzers, tests, restore) whose output the worker reads; it runs sandboxed with no credential but the
  router key, is the code under review or a pinned dependency, goes back through review and CI, and tainting it would leave no
  worker able to push.
- The orchestrator pushes only to `factory/*`, with a repo-scoped GitHub App installation token passed
  via git env config (never argv/remote URLs/.git/config) on every network git call. Only the merge gate merges (below).
  Tokens are minted fresh per call (never cached) and refused if they outlive 1 hour (`GitHubApp`).
  Server-side, `github-repo protect` (`RepoProtection`) applies rulesets: default branch needs a PR, and only
  repo admins may write refs outside `factory/**` (needs GitHub Pro for private personal repos).
  With the gate App's id known (`github-app setup --gate`), that ruleset also lists the gate App as a bypass actor in
  `pull_request` mode only: it may merge a PR past it, never push.
  The main ruleset's `pull_request` rule sends every parameter GitHub takes (`RepoProtection.MainPullRequest`: 0
  approvals, no stale-dismiss/code-owner/last-push/thread-resolution, `require_extra_approval_for_unattributed_changes:
  false`, `required_reviewers: []`, `allowed_merge_methods: merge, squash, rebase`) — a left-out one takes GitHub's
  default, and the unattributed-changes default (`true`) blocked every PR of unsigned (worker) commits. After each
  write, `protect` reads the ruleset back and exits 1 naming every field or parameter GitHub holds otherwise (including
  one it stores that was not sent). Re-running `protect` overwrites same-named rulesets (PUT), so it repairs them; of
  several existing rulesets with one managed name only the first is written, and `protect` prints a `warning:` naming the
  other ids, which it leaves unchanged (they still apply; delete stale ones by hand).
  That `~ALL` ruleset also stops non-admin integrations (Dependabot, `GITHUB_TOKEN` Actions deploys such as
  `gh-pages`) from writing any branch outside `factory/**`.
  Workers get no git/gh tools and run as `_factory`, which cannot reach the owner's keychain (App key) or
  gh/git credentials. Owner-side git on a worktree always passes
  `--git-dir`/`--work-tree`, with the admin dir found from the clone's side (never the worker-writable `.git`
  file, also on resume), and every owner-side git call (all go through `GitWorkspace`) is isolated from the
  owner's own git config by `Git/OwnerGit.cs` (`GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL=/dev/null`, `GIT_LFS_SKIP_SMUDGE=1`;
  `-c core.hooksPath=/dev/null`, `core.fsmonitor=false`, `rerere.enabled=false`, LFS filters emptied), so no hook, filter or
  merge driver a PR's files or `.gitattributes` select runs as the owner; worktrees live under the owner-owned work root (root-owned parent), are shared with
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
- Stuck workers (sc-25388, `Sessions/StuckDetector.cs`): every implementer/fixer session's stdout is folded, line by line, into
  turns (one assistant `message.id`: its text, each tool call's name + input JSON and that call's `tool_result` with its error
  flag; thinking and the router's banner/feedback footer left out). Parts are normalised separately: tool name + input collapse
  whitespace only (digits kept: `Step1.cs` ≠ `Step2.cs`); text and results also make digit runs `0`; each result's outcome (error
  flag + pass/fail/error word counts) must match exactly. Similarity is character-trigram Dice over the whole normalised part (no
  head/tail cut). Two turns are the same when outcomes match and calls, and text+results, are each ≥ `Worker:StuckSimilarity`;
  their tool calls are the same when both made calls, outcomes match and calls and results (text left out) are each that alike.
  A session is stuck when, for a cycle length p = 1…4, its last `Worker:StuckRepeats` × p complete turns repeat one p-turn cycle
  — each turn the same as (or with the same tool calls as) the turn p later — the whole recent sequence, so the same test
  command after different edits, or with a different result, is not a loop; the same failing call narrated differently is. Only
  the last `StuckRepeats` × 4 complete turns are kept. Only complete turns (all tool results in,
  or a later turn started) are compared, so it trips on the last repetition's result. Then `stuck` (Detail: why, tool names only)
  is checkpointed before the worker is interrupted at its next tool boundary through the Pause mechanism (`IWorker.RequestPause`;
  a Continue does not withdraw it; the pause grace stops a worker that ignores it); the session ends `stuck` (cost fetched) and
  is never resumed. Its round fails: in Implement (no fix rounds) the worktree is removed and a `stuck-retry` checkpoint starts a
  fresh session in a fresh worktree, and the second stuck session since entering Implement (`RunPipeline.MaxStuckImplementSessions`)
  escalates; in a fix round nothing is pushed, the worktree is removed and the round ends at its next state with the head
  unchanged (Review records a failed `fix-progress`, CI sees the same red CI, the merge gate the same conflict), which then
  dispatches the next round or escalates at the fix-round cap — the stuck round counted when it started (every cap escalation
  lists each round since the last Implement, of any kind, whose fixer was stuck, with its reason: `RunPipeline.StuckRounds`). A fresh implementer after
  `stuck-retry` gets one prompt line naming the repeated tool calls (names only, E4). A worker that
  finishes on its own after the detection is done, like one after a pause. Silence never counts: the dashboard marks a running
  session quiet (pipeline `(live, quiet)`, session page `quiet: no event for …`) after `Worker:QuietMinutes` without an event, and
  nothing else happens. The triage worker (read-only, no round) is not watched.
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
  model list (`Review:<Role>:Models`, else `Review:Models`, default `claude-opus-5`) that is a Claude Opus 5 or newer (owner decisions 2026-10-08 and 2026-10-09:
  every reviewer and second model is Claude, whichever models the implementer used — there is no cross-family rule;
  `ReviewModels`/`ReviewerChoice`; a role with no such model escalates before any call). Each role makes one
  router call (`RouterReviewer`: `POST /v1/messages` with `"stream": true` — the router cancels a call that has sent its
  client nothing for 10 s — assembled by `MessageStream`, which fails closed on an `error` event, a stream without
  `message_stop` or one silent for 2 min; router key only, `x-weave-force-model` pin, its own
  `X-Claude-Code-Session-Id` so the pin and the cost stay scoped to it) whose system prompt is the role's prompt file and
  whose message holds the story, the base commit's file list (`IGateGitHub.GetFilesAsync`) and the diff of the PR's head
  commit. Prompts: `factory/prompts/{correctness,spec-conformance,security,confirm}.md` in this repo, compiled in as
  embedded resources (`ReviewPrompts`) — never read from the target repo or the PR (which could rewrite the prompt it is
  judged by) nor from disk at run time; changing one is a dark-factory PR. Each role answers findings tagged `blocking` or
  `optional` (an unknown severity counts as blocking); each blocking finding goes to a second model (`Review:Confirm:Models`:
  the first Claude model not pinned to the reviewer's pinned id (`ReviewModels.SamePinned`: same id or its dated snapshot) and not one the router may serve as the model that served the review (`ReviewModels.Serves`: with the reviewer pinned `claude-opus-5-5`, a `claude-opus-5` pin may be upgraded to it, so the default list picks `claude-sonnet-5`); none → escalate) with `confirm.md`: not confirmed → downgraded to optional (`downgraded: true`), does not
  block; confirmed, or an unusable confirm answer → stays blocking. Every panel call's session id is generated by the
  pipeline and checkpointed (`review-session`: "session model sha role prompt-path@sha256:hash"; role `confirm-<role>` for
  a second model) before the call, so a call that never returns still has a readable cost (E9) and the prompt it used is on
  record; it never goes in a row's `ClaudeSessionId`, which stays the implementer's (outcomes, escalation comments and
  stops read the last one). A Pause/Stop is checked before each panel call. The verdict (`ReviewPanel.Decide`,
  deterministic: pass only when every required role answered cleanly and no finding is blocking after confirmation; it
  lists the risky paths and every role's model, served model, session, prompt and findings) is a `verdict`
  checkpoint bound to that head SHA. Anything but a clean findings line from the pinned model is an unusable review and a
  fail: the router must name the served model and it must be the pinned id or its dated snapshot (`<pinned>-yyyymmdd`,
  `ReviewModels.Serves`), or — only when the pinned id is a Claude Opus — a Claude Opus of the same or a higher version (the
  router may upgrade, e.g. `claude-opus-5` served as `claude-opus-5-5`, never downgrade); a non-Opus pin such as
  `claude-sonnet-5` counts only as itself or its snapshot. A router answer naming no served model, a lower Opus, a non-Opus
  or a non-Claude model fails closed. Fix loop (sc-25380, `Gate/FixLoop.cs`, `FixAsync`): a fail whose
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
  is a failed round. Otherwise a failed round. Every round counts against the fix-round cap: the lower of the base's
  `factory/gate.yaml` `risk.max_fix_rounds` (1–3: it can only lower the hard cap, never raise it) and `Lifecycle.MaxFixRounds`
  (3), read fresh at each review, CI and conflict cap decision (`FixCapAsync`; an unreadable or invalid policy leaves the hard
  cap, and the merge gate blocks on it anyway) and checkpointed (`fix-cap`, before the decision) whenever it differs from the
  one in effect (`RunPipeline.FixCapOf`: the latest `fix-cap`, else 3), so fixer prompts, logs and reports (`Fix rounds: n of
  cap`) render the effective cap. A fail that would need a round past the cap escalates with the open findings listed in the
  comment. Every cap escalation (review, CI, conflict) has the same words (`FixCapReachedAsync`): the rounds used, the cap and
  where it came from, "one count shared by review, CI and conflict fix rounds", and the stuck rounds.
  CI self-heal (sc-25383, `Gate/CiHeal.cs`, `CiFailedAsync`/`CiFixAsync`): once every check on the reviewed head has
  finished and CI is red, the failure is triaged (`CiHeal.Triage`) against the PR's base commit's CI and checkpointed
  (`ci-failure`, `CiTriage`: names and conclusions only, never log text). A check that failed with `failure`/`timed_out` and
  is not red on the base is the PR's; one also red on the base, or one CI did not run to a result (`cancelled`, `stale`,
  `action_required`, no conclusion, a suite's `startup_failure`), is not — then the item escalates naming it, with no fixer
  and no round spent on infrastructure. Exception: a `cancelled` check or suite next to a failure that is the PR's is
  excused (a matrix's fail-fast cancels the other legs), recorded in the triage (`cancelled`), and CI on any later head
  waits until every check a triage named (fixable or cancelled) has reported there (`CiHeal.Unreported`) — so it must run
  and pass, not vanish. Otherwise CI → CIHealing (row Detail = the fixed head), a fix round that shares the
  count and the cap with review rounds (`TransitionContext.IsFixRound`; the effective cap above applies to both); at the
  cap it escalates listing the failing checks. The CI fixer runs exactly like a review fixer (`RunFixRoundAsync`, same
  sandbox/router key/resume/worktree rules) and gets the story plus each failing job's log read from GitHub when a fresh
  session starts (`GET …/actions/jobs/{id}/logs`, last 256 KB, needs the gate App's **Actions: read**; without it the check
  run's output and annotations, `Checks: read`, also used when the log read fails or exceeds `LogReadTimeout`, 60 s
  including the body), turned into an excerpt (`CiHeal.Excerpt`: ANSI/timestamps stripped, ends at
  the last `##[error]`, ≤ 6000 chars, at most 4 checks) with credentials redacted (`CiHeal.Redact`) and fenced in
  `<ci-log>` blocks. Its push → CI, which waits for the PR to show it and sends it to Review (no verdict: E3). That review
  re-runs every role whose scope the fix's own diff (fixed head → pushed head) touched (`CiHeal.TouchedRoles`: correctness
  and spec conformance for any file, security only for a path that calls it in) and carries the rest; then CI again (green
  → MergeGate). No `fix-progress` is recorded for a CI round.
  The router refusing a call for usage (429/529 or its
  exhaustion/rate-limit body: `RouterUsageLimitedException`) pauses the factory for usage (`reviewer-rate-limited`) like a
  worker's exhaustion; the item resumes and the head is reviewed again once it lifts. CI polls the head's check runs,
  commit statuses and check suites until finished (none at all keeps waiting until `Gate:CiTimeoutMinutes`); red goes to
  the CI self-heal above (CI not read in full escalates). A queued/in-progress check suite (a workflow registered but without check runs yet) keeps CI pending; a suite
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
  every reviewer is a Claude Opus 5 or newer and every second model a Claude model pinned to another id than its
  reviewer's and, when both served models are known, served as another model than its reviewer (an Opus upgrade must not
  make one model confirm its own finding), each served by the router as pinned or, for an Opus pin, as a same-or-newer Opus — `ReviewModels.Problems`; the implementer's models are not consulted), `security-review` (the verdict has the
  security review), `risk-threshold` (changed lines, files — renames count both paths — and fix rounds within `risk`),
  `new-tests-fail-on-base` (sc-25382, below). A sealed path always escalates (even with no verdict); a protected one escalates after its checks instead of merging; each
  evaluation is a `gate` checkpoint. A head without a verdict (a push after the review) goes back
  to Review (CI/MergeGate → Review are table rows); otherwise any failed rule escalates. A pass checkpoints `gate-passed`
  with the head SHA and merges with the gate App's write token and `sha` = that head (GitHub refuses a moved head: 409 →
  Review); the Merge row's detail is the merge commit. A PR found merged — on resume after a crash or Ctrl-C during the
  merge call, or re-read after a merge call that failed (e.g. timed out) — is recorded as Merge if its head is one some
  `gate-passed` names (anywhere in the item's history), else escalates. Merge reports the board Merged (`merged-reported`), then
  Watch (no handler yet). Gate reads use a read-only gate-App token; only the merge mints a write one.
  Merge queue (sc-25384, `Gate/MergeQueue.cs`, `RunPipeline.MergeQueue.cs`): a gate pass is an approval, not a merge. The
  item joins its repo's queue (`queued` checkpoint; FIFO by that row, derived from the ledger alone, so durable and shared
  by every process) and one item per repo holds the turn (`queue-turn`), taken only by the queue's head while nobody holds
  it, under a Postgres advisory lock keyed by the repo (`MergeQueue.LockKey`: negative, never an item id). Any other run
  ends with the item left in MergeGate (`[queue] … waiting for sc-N`; no error), and a later poll retries. The turn is held
  until the item leaves MergeGate; a crash or an `interrupted` pause keeps it (and the place), a user's or the usage pause releases it
  (the place is kept: `MergeQueue.Member`). An item a control holds (Pause on the factory, its epic or itself, the usage pause,
  Stop) whose run lock is free is left out of the queue while held, even before any ledger row says so, so it never holds up
  its repo; one whose run is still active stays in until that run records the pause. Back from the control it keeps its
  approval-time place but never preempts a turn taken meanwhile (the newest `queue-turn` row is the repo's). In its turn the item, each step a ledger row first: compares the head with the base branch
  (`IGateGitHub.CompareAsync`); behind → the orchestrator merges the base into a fresh worktree of the branch owner-side
  (`IRepoWorkspace.MergeBaseAsync`; no worker runs git), checkpoints `base-update` (old head, base, merge commit) and only
  then pushes it as a fast-forward (`PushAsync`, never forced) with the factory App's token — the `factory/**` rulesets
  already let that App write the branch, and only the gate App merges into the base. The new head's verdict (E3): when the
  PR's diff against that base is byte-identical at the new and the reviewed head, the proof (`review-carried`: both SHA-256
  hashes) is checkpointed and the verdict recorded for the new head with every review `carried`; otherwise the panel
  re-reviews the roles the update's own diff touched (`CiHeal.Carried`), and a fail → Review (fix loop). Then CI on the
  updated head (red → MergeGate → CI, i.e. CI's triage/self-heal; the item leaves the queue), the gate evaluated fresh, the
  base compared again (moved meanwhile → update again), and the merge of exactly that head. A conflict with the base →
  `merge-conflict` (files) and MergeGate → Fixing, a fix round sharing the count and cap; that round's fixer gets a worktree
  where the orchestrator has merged the base (`base-merged`), resolves the conflict markers, its push completes the merge
  (a marker left in a conflicted file of the pushed commit escalates) → Review of the merge commit in full. Residual race:
  only an admin can move the base between the last compare and GitHub's merge (the merge binds the head SHA, not the base).
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
  head already has a verdict recorded under the dropped cross-family rule (e.g. a `gpt-5.5` or `claude-opus-4-7` reviewer)
  is reviewed again once by the current panel (`MergeGate.Superseded`: its models break `ReviewModels.Problems` and it is
  the only verdict on that head — at Review it is not reused; at MergeGate the gate answers ReviewHead when those are its
  only reasons), then merges as usual; the current panel's own verdict is a second one on the head, so if its models still
  break the rule the gate escalates instead of reviewing again. See docs/acceptance.md.
- Processes migrate the ledger through `LedgerMigrations.MigrateAsync` (advisory-locked: EF alone lets two concurrent
  migrators apply the same migration) before reading it; tests migrate their temp database before starting a host.
- Tests: xunit.v3 on Microsoft.Testing.Platform (`global.json` opts in).
