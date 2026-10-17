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
  `IWorkSource` + the `factory work` intake loop (`WorkSources/`; registered on the `factory work` host by `FactoryHost.BuildWork` via `services.AddIntake(options)`),
  the one registry of work sources `WorkSources/WorkSourceRegistry.cs` (sc-25982: which sources are configured, each built once; the
  only file outside `Shortcut/` that names the Shortcut source's type)
  and the Shortcut adapter (`Shortcut/ShortcutWorkSource.cs`), GitHub issues as a second source (`Issues/`: the intake
  `IssueIntake` — poll, triage, route, approvals — the routing rule `IssueTriage.cs`, the triage worker `TriageWorker.cs`,
  `GitHubIssueWorkSource`; GitHub side `GitHub/GitHubIssues.cs`), the Pause/Continue/Stop controls (`Controls/`), and the
  review and merge gate (`Gate/`: `ReviewModels` (which panel calls count), `ReviewPanel` (roles, prompts, findings),
  `Reviewer.cs` (`IReviewer`/`RouterReviewer`) reading the router's stream with `MessageStream`, `GatePolicy` (+ `RiskyPaths`)
  and the path tiers `PathTiers`, `MergeGate`, `FixLoop`, `CiHeal`, `MergeQueue`, the test-only `GateCheckSeam`, the new-tests
  check `NewTests.cs` (`NewTestsCheck`)/`XunitNewTests`/`CargoNewTests` (Rust, over the lexer-level `RustSource`)/`SandboxTestRunner`,
  the plan-time check of a repository's CI workflow triggers `CiTriggers.cs` (sc-25873; pipeline side `RunPipeline.CiTriggers.cs`);
  reviewer prompts in `factory/prompts/`; GitHub side
  `GitHub/GateGitHub.cs`; pipeline handlers `RunPipeline.Gate.cs`, the merge gate and queue `RunPipeline.MergeQueue.cs`),
  the MCP upstreams and the loopback MCP proxy (`Mcp/`: `McpHttpClient`, `McpUpstream`/`McpUpstreams`/`McpServers`
  (allowlists), `McpProxy`, `McpProxySessions`; CodeGraph's client `CodeGraph/CodeGraphMcpClient.cs`; sc-25707),
  plan items (`Plans/`: `Plan.cs` — the typed plan, `PlanParser`, the `plan` label and the planned-story marker — and
  `PlanWorker.cs` — `IPlanRunner`, `WorkerPlanRunner`, `PlanPrompt`; `PlanRelease.cs` — the release decision and the one "blocker
  satisfied" rule (sc-25871), and the classification of a plan's stories with what a failed one holds (sc-25876); `Changeset.cs` — a plan's changeset branch and the changeset review's spec (sc-25872); pipeline handler `RunPipeline.Plan.cs`; the production seat `SandboxPlanRunner` in `FactoryRunner.cs`;
  sc-25870),
  and the outbound gateway (`Gateway/`: `OutboundHttp` builds every GitHub, Shortcut, router, CodeGraph and Kanban `HttpClient` (and the
  acceptance tests' dashboard client and their loopback-only client of the MCP proxy, `LoopbackMcp`); the GitHub client's reads
  (GET/HEAD) answered 502/503/504, failed in the network or with no headers within 30 s are sent again, at most 3 attempts with a
  1 s then 2 s backoff, the last attempt's answer the caller's as before — writes are never resent (`GitHubReadRetry`, sc-25992: a
  single 503 on a pull request read escalated an item live); `GitRemoteWrites` holds every `git push`'s arguments and git's installation-token
  credentials; `GitRemoteReads` the github.com remote URL, every clone/fetch's arguments (sc-25391) and the repository a github.com
  remote URL names (`RepoOf`, sc-25981); `KanbanWrites` builds the one MCP client that holds the orchestrator's full-scope Kanban
  token (sc-25981); `GitHubCli` is the only `gh` run),
  and KanbanBoard as a third work source (`Kanban/`, sc-25981: `KanbanWorkSource`, its typed MCP caller `KanbanBoard.cs`, and
  `KanbanKeys.cs` — the ledger's `kb-<n>` keys of items and of epics, the watch scope `KanbanScope`, the web links `KanbanLinks` and the dashboard's `KanbanLedgerLinks`),
  and autonomous hot-file recovery (`Recovery/`, epic sc-26335: `HotFileRecovery` — detection, the repository hold, the
  assessment stage and its scheduler, the correction grant checks —, its budgets and refreshes `RecoveryProgress.cs`, post-delivery
  reassessment and redirects `RecoveryContinuation.cs`, the immutable evidence `RecoverySnapshot.cs`, the assessor
  `RouterHotFileAssessor.cs` with its pinned read-only tools `RecoveryAssessmentTools.cs` and the answer's parser
  `RecoveryDecision.cs`, the correction items `RecoveryCorrections.cs` and their internal work source `RecoveryWorkSource.cs`
  (`rc-<n>`), the operator read model `RecoveryStatus.cs`, the human re-arm `RecoveryReassessment.cs` and the incident state
  rows `IncidentTransitions.cs`; pipeline side `RunPipeline.Recovery.cs`; "Hot-file recovery" under the invariants). `RepoRef` and
  `RepoResolver` (`RepoRef.cs`) are in the root namespace: every source and the pipeline use them.
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
  anywhere, the embedded manifests, prompts, scripts and Razor markup included; DF0004 GitHub's or Shortcut's API host or the hosted CodeGraph's or KanbanBoard's MCP host, or a
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
- `scripts/` — `setup-worker-user.sh` (one-time root setup of the sandbox users: `_factory`, and with `--slots n` also
  `_factory2` … `_factoryn`, one per worker slot, sc-25989) and `factory-worker-launch` (the helper every sandboxed worker runs
  through, installed root-owned once per sandbox user, each copy pinned to its own user and uid).
- `tests/DarkFactory.Orchestrator.Tests` — unit tests (no network; fake HTTP APIs, InMemory EF, local git).
  Tests run `scripts/factory-worker-launch` only through `SafeHelper` (a checked copy whose every signal goes through a
  seam that signals only the test's own processes, and whose uid-sweep listing names only the helper's tree and the test's
  registered pids; a dead target — `ps` state `Z`, or `X`: on Linux a process its parent is reaping right then is listed with
  parent 0, which once refused the helper's own watcher, exit 99, sc-25992 — is skipped, never signalled, and a dead group
  member does not count; `SafeHelper.Source` is private); `SafeHelperTests` fails if a test
  reaches the real helper another way (a repo path, the installed path, `Worker:LaunchHelper`/`options.WorkerSandbox`/`options.WorkerSlots`, sc-25992).
  A test signals a process only through `OwnProcess` (pid + start time, recorded where the pid cannot have been reused;
  `KillIfStillRunning` signals only while the start time still matches); `OwnProcessTests` fails on any other kill in
  `tests/` (`Process.Kill`, a kill(2) import, a `kill`/`pkill`/`killall` in a script).
  The helper's stderr (sc-25877's flake, then sc-25989 and sc-25990; all `helper_version` 4, which was never installed without
  them): it holds only the worker's own output and the helper's `factory-worker-launch: …` messages, never a report of bash's
  own. bash 3.2 writes three of those on its stderr: `… Terminated: 15 ( while … )` and `… Killed: 9 /usr/bin/env -i …`, the
  job report of a job a signal ended (the watcher at the end of every run; the worker on every Stop), written when it next
  reads a script line or finishes a command — about 1 in 100 runs of any test asserting an empty stderr, and for a worker a
  signal ended BEFORE the helper's main shell had reached its `wait` no redirection on that `wait` can catch it; and, under
  load (about 1 in 650 helper runs), `child setpgid (N to N): Operation not permitted` — under `set -m` bash and the child it
  forks both call setpgid for the child, the child's call can lose on macOS, and bash's child reports it before the command's
  own redirections apply (the group is set all the same). The design (sc-25990): right before the launch the helper moves its
  own stderr away for the rest of the script — `exec 3>&2 2>/dev/null` — so everything bash itself reports from then on goes
  nowhere; the worker's own redirection gives it the real stderr back before its exec and closes the saved descriptor for it
  (`"$@" </dev/null 2>&3 3>&- &`: the worker's stderr and a failed exec's message still arrive, and the worker holds
  descriptors 0, 1 and 2 only); the one message the helper prints after the launch (`… processes still appearing after N
  sweeps`) goes to the saved descriptor by name (`>&3`); and fd 3 is closed for everything else the helper starts — the watcher
  (`) <&0 3>&- &`, whose own stderr is now `/dev/null` too) and the sweep's listing and its awk (`list_uid_procs 3>&- |
  outside_tree "$$" 3>&-`) — so nothing but the worker's own tree can hold the stderr the orchestrator reads to its end.
  Everything printed BEFORE the launch (usage, a refused variable, `refusing the _factory uid sweep: …`) is on the real stderr
  as before. The earlier redirections (`{ send_signal "$watcher" || true; wait "$watcher" || true; } 2>/dev/null`, `wait
  "$worker" 2>/dev/null`) stay. What is lost with it: bash's own diagnostics after the launch (a fork that fails, a `set -e`
  exit); the helper then exits non-zero. No safety property changed: the `sandbox_user`/`sandbox_uid` checks, `send_signal`,
  the sweep and its self-exclusion, the environment, the program check and the exit status are the same lines as before.
  `WorkerSandboxTests` pins that text; runs the helper 200 times with an empty stderr; stops a worker at once and later (exit
  137, empty stderr, the stderr ending with the helper) and at once 200 times; holds the main shell 0.3 s in front of its
  `wait` (a mutation of the test copy) while the worker ends itself by a signal (a file size limit of 0: SIGXFSZ, exit 153) or
  is killed by a Stop — the early report made certain: with the helper's stderr still its own both print bash's report every
  time, now neither does; and checks that the worker, the watcher and the sweep's listing hold no descriptor a plain child of
  the test host does not, and that the helper's own late message still arrives next to a killed worker. Measured (sc-25990,
  under CPU load the measurement started and stopped itself): the new helper 6,000 stop-at-once runs, 6,000 whole runs with
  a sweep and 6,000 self-signalled workers, no line on stderr. The helper as it was: the early report did not show by timing
  alone (0 in 6,000 stop-at-once runs with its watcher's stderr silenced, 0 in 6,200 self-signalled workers, the latter up to
  36 load generators on 18 cores) — the window is a few script lines wide — and shows in every run once the main shell is
  held in front of its `wait`; what did fail every stop-at-once loop within 18 runs was the TEST seam's own `awk: i/o error`
  from a watcher killed mid-`send_signal` (the seam classifies targets with awk; the production `send_signal` is the `kill`
  builtin and prints nothing), on the watcher's stderr, which was the helper's real one. Never loosen those tests' assertion.
  `CrashResumeTests` also needs the compose Postgres (skips locally without it, fails under `CI`): it
  SIGKILLs `tests/DarkFactory.CrashHost` (the real pipeline with a fake `claude` script) and restarts it.
  `ShortcutContractTests` replay recorded Shortcut API fixtures (`Fixtures/shortcut`, strict request matching);
  re-record with `SHORTCUT_RECORD=1` (writes only throwaway `[dark-factory fixture]` stories, archived after).
  `KanbanContractTests` (sc-25986) do the same for KanbanBoard's MCP: the Kanban work source's whole lifecycle — list ready,
  claim and read back, spec with epic and documents, the status changes, comment, links, release, claim renewal, a lost claim,
  a plan's items with a dependency and their retry, blockers, make-ready, the return of an unstarted item, the epic key read,
  a `busy` retried and a `conflict` re-read — as seven scenarios (`KanbanContractTests.Scenarios`), each replayed against its
  fixture in `Fixtures/kanban` by `KanbanReplayHandler` (`Support/KanbanFixtures.cs`) under `McpHttpClient`. Matching is strict
  per `tools/call`: the tool name and deep-equal arguments, in order (a tool or argument the recording does not have, a missing
  one, another value, an extra call, a recorded call never made: each fails, naming the call). The transport is normalised,
  not recorded: no JSON-RPC envelope, no session id (the replay hands out a fresh one per session and checks the handshake —
  bearer token on every request, `initialize`, `notifications/initialized`, the closing `DELETE`, no other method), no framing
  (every fixture is replayed answered as JSON, as an event stream, and alternating). A `KanbanInteraction` is
  `{tool, arguments, isError, answer}`: the answer is the text block's one JSON document, a refusal's `{code, message}`, or a
  plain MCP error's text. Recording: `KanbanRecordingHandler` (same file) under the adapter, `KanbanFixtureRecorder`
  (`Support/KanbanFixtureRecorder.cs`) around it — one throwaway project created under an idempotency key
  (`[dark-factory fixture] contract recording`, key `DFFIX<8 hex>`) with an epic, a document and the scenarios' items, deleted
  afterwards (`delete_project`, by the id the board returned to this run: the recorder's only hard delete; archived when the
  delete is refused), on a token of its own (`KanbanFixtureRecorder.CleanupTimeout`, so a cancelled run still cleans up), then
  the board read again and anything of its own neither deleted nor archived fails the run naming it (a delete whose answer was
  lost is found gone by that read; a clean-up that itself fails names the project). What an earlier run that died left is
  never deleted (`SweepAsync`): a project with BOTH prefixes that is exactly a recording's (`NotARecordingAsync`: that name,
  the key `DFFIX` + 8 upper-case hex, every item, epic and epic document in it prefixed; the contract gives a project no
  creator to check) is archived — reversible — and named (key, id) in `Notes`, which `Record` prints as `[kanban fixtures] …`
  for the owner to delete by hand; one that is not exactly that is left untouched and fails the run before anything is
  written. The repository is public: a recorded listing keeps only the recording's own projects, items and documents, an
  answer about anyone else's object (and the change feed) is refused, an identity's name becomes `fixture-recorder` and its id
  a fixed placeholder (`f0000000-0000-0000-0000-00000000000N`; `KanbanIdentities`, one set for the whole run, seeded from the
  recorder's own first claim, so a refusal's message naming the identity is scrubbed in every scenario), an e-mail a
  placeholder (replaced in string values, never in JSON text), and no request header (the token) is ever read into a fixture;
  `KanbanFixtureGuard` fails a committed fixture that holds a token (`Authorization`, a bearer value, `kbp_…`, a JWT), an
  e-mail, an identity's name, an identity's id that is not the placeholder, a refusal message naming an identity the set or
  the run knows (`SetProblems`), or an item/project/epic/document/listing entry that is not a throwaway of the fixture's own
  project. Before `Record` saves, `KanbanFixtureRecording.VerifiedAsync` also searches every file for the literal token and
  replays every fixture in every framing, and `KanbanFixture.SaveAll` replaces the set whole or not at all. The `conflict` is real (the recorder edits the item between the adapter's read and its
  conditional move); a `busy` cannot be provoked on demand (the board answers it only after a write waited 10 s for other
  writers), so that one interaction is authored from the contract's Error shape by the recording handler, not forwarded, and
  marked (`note`, `KanbanRecordingHandler.SyntheticBusyNote`) — the resend after it is the board's own answer.
  HOSTED (sc-25992, 2026-10-14): the committed fixtures say `"recorded_from": "hosted KanbanBoard"`; before, they were
  provisional recordings against `FakeKanbanBoard` (`KANBAN_RECORD=fake`, which `KanbanFixtureRecording` still proves end to
  end in every `dotnet test`). A refusal's message is scrubbed of an identity's name only where the name stands on its own
  (`KanbanIdentities.Mention`: the factory's identity is called `factory`, and a plain replace rewrote `dark-factory:<id>`
  markers). Re-record against the hosted board with `KANBAN_RECORD=1` and a full-scope token in `KANBAN_RECORD_TOKEN` (the environment
  only: never the keychain, never a file; `KANBAN_RECORD_URL` for another instance: absolute https, or http on a loopback
  host, else refused — `KanbanFixtureRecording.Endpoint`) — docs/acceptance.md, "Kanban contract
  fixtures". `KANBAN_FIXTURES_REQUIRE_HOSTED=1` (set in CI) makes a provisional fixture fail `dotnet test`.
- `tests/DarkFactory.AcceptanceTests` — live epic acceptance harness; skips unless `FACTORY_E2E=1`. AT1–AT8 runbook:
  `docs/acceptance.md` (the `factory work` tests use a throwaway ledger DB and their own work root, `E2e.cs`). Phase 2's epic
  AT2/AT4/AT5/AT6 (sc-25391): `ReviewGateTests` (typed outcome per row, merge queue, PR body and closeout equal to the ledger's),
  `GateNegativeTests` (sealed path, corrupt policy on a throwaway branch, a new test passing on the base, a 4th fix round; review
  panel replaced by `SeededReviewer`, seeding through `SandboxRepo`), `FreezeAndStuckTests` (freeze seeded in the throwaway
  ledger; a stub Claude CLI replaying the stuck/silent fixtures), `IssueTriageTests.A_triage_session_that_read_issue_text_is_tainted_and_cannot_push`.
  Phase 4a's P4-AT1 (sc-25870, sc-25871, sc-25872, `PlanAcceptanceTests`): a real planner plans a `plan`-labelled story into Backlog
  stories — at least 3 with a dependency chain, else the run fails INCONCLUSIVE (`PlanLive.ChainProblem`) — created once, then
  released in dependency order; each released story is really worked and merged by the gate into the plan's changeset branch, and
  the plan item then takes the changeset to the default branch by one gated pull request and is closed out, the plan's and each
  story's closeout on the board equal to `LedgerReport`'s rendering of the throwaway ledger's rows; a sibling test
  (`P4_AT1_factory_work_…`, `FACTORY_E2E_P4_WORK_PLAN_STORY` + the watch scope) runs a whole plan through the real intake loop
  (`FactoryHost.BuildWork`): the plan item picked up in the watch scope, its stories released into it and picked up from
  `ListReadyAsync` (sc-25877). P4-AT2 (sc-25873, `MultiRepoPlanAcceptanceTests`, `FACTORY_E2E_P4_MULTI_PLAN_STORY` + `FACTORY_E2E_P4_SECOND_REPO`):
  a plan across two repositories — a changeset branch in each, each story's pull request in its own repository, the changesets
  merged in the plan's order; INCONCLUSIVE unless a story waits on a changeset (`PlanLive.ChangesetBlockerProblem`). P4-AT3
  (`PlanAcceptanceTests`): the proxy part (sc-25874) — the production proxy with a planner
  grant over two repositories (`FACTORY_E2E_P4_PLAN_REPOS`) answers for each and refuses a third project, a call naming no
  repository and off-list tools — and the three refusals (sc-25877), each Escalated with nothing created on the ledger or the real
  board: a plan with a cycle and an oversized plan (`FACTORY_E2E_P4_CYCLE_STORY`, `FACTORY_E2E_P4_OVERSIZED_STORY`; a stub Claude
  CLI answering a fixed plan, `StubClaude.Answering`, through the production planner seat) and a `Repo:` line naming an
  unconfigured repository (`FACTORY_E2E_P4_UNCONFIGURED_STORY`; no session at all). `PlanLiveTests` (not live) check those
  fixtures and the INCONCLUSIVE rules in every `dotnet test`. P4-AT5 (sc-25876, `PlanFailureAcceptanceTests`, `FACTORY_E2E_P4_HOLD_STORY`): a fixed plan's
  stories on the real board, their progress seeded in the throwaway ledger (the plan item with its `changeset`/`changeset-pushed`
  rows, as production leaves it) — an escalated story holds its dependent, the plan item
  is commented once, the dashboard data groups the stories, and Stop on the plan item stops its stories and cancels it; no model.
  Phase 3's epic AT1–AT5 (sc-25710, `CodeGraphAcceptanceTests.cs`): `CodeGraphReviewTests` (the real panel's tool calls, turns and
  overlay-bound CodeGraph answers), `CodeGraphTriageTests` (a triage session through the proxy, scanned for the CodeGraph token by a
  wrapper Claude Code path, `ScanWrapper`; the production proxy refusing off-profile tools; a live Kanban read through the reviewers'
  tools), `CodeGraphGateTests` (a critical file escalating on risk-threshold; CodeGraph unreachable blocking at the CI timeout).
  Phase 4a's P4-AT4 (sc-25875): `GateNegativeTests.P4_AT4_…` (a seeded cargo package on the PR branch: the new-tests check run by
  cargo as the worker user; needs cargo and rustc for `_factory`) — the rejection (a test that already passes on the base) and
  the merge (sc-25877, `FACTORY_E2E_P4_CARGO_MERGE_STORY`: a test of new code, `not-built` on the base and passing on the head,
  `new-tests` `pass`, a `Merge` gate row, `gate-passed`, the pull request merged; it leaves a crate per run under the sandbox's
  `e2e-rust-merged/`).
  Phase 4b's P4b-AT1 (sc-25981, sc-25992, `KanbanAcceptanceTests`, `FACTORY_E2E_P4B_KANBAN_ITEM` + `FACTORY_E2E_P4B_KANBAN_PROJECT`):
  one `factory run kb-N` takes a Ready item of a watched KanbanBoard project to a merged pull request with no human — its key
  given once, claimed on the real board (the factory's marker), Intake → Implement … MergeGate → Merge → Watch with typed
  outcomes, the pull request from `factory/kb-N` merged at the Merge row's commit, the item `Done` with the pull request and the
  branch linked under their kinds and exactly one comment equal to `LedgerReport.MergedCloseout`'s rendering of the rows before
  the posted `closeout`; a second run writes no claim, link or closeout again. P4b-AT4 (`KanbanAcceptanceTests.P4b_AT4_…`):
  `factory work` with KanbanBoard alone claims the item and opens its pull request (on GitHub, linked with the branch), then
  `factory stop --item kb-N` stops it. P4b-AT7 (sc-25992, docs/acceptance.md "P4b-AT7 — defaults"): one live test per earlier
  phase re-run with default settings; `FACTORY_E2E_P4B_DEFAULTS=1` makes the shared harness (`E2e.StartAsync`, `E2e.Options`)
  fail a test before it starts unless `Kanban:Watch:Projects`, `Kanban:WebUrl` and a Kanban write token from config or env are
  unset (a keychain write token is hidden from the run's options) and `Worker:Slots` and `Intake:MaxConcurrentRuns` are 1
  (`E2e.DefaultsProblems`; `DefaultsGuardTests`, not live). Every live test SKIPS when its opt-in, a credential or a variable is
  missing (the convention since Phase 1, kept): skipped is not passed.
  P4b-AT2 (sc-25984, `KanbanPlanAcceptanceTests`, `FACTORY_E2E_P4B_PLAN_KANBAN_ITEM` + `FACTORY_E2E_P4B_KANBAN_PROJECT`): a real
  planner plans a `plan`-labelled Kanban item into at least 3 items with a dependency chain (else INCONCLUSIVE,
  `PlanLive.ChainProblem`) — each one item of the real board, in the plan item's project and epic, in `Backlog`, marked as planned
  from the plan item's GUID, with the board's own dependencies — released in order (`Ready` only after its blockers' Merge rows),
  each worked and merged by the gate into `factory/changeset-kb-<n>`, the changeset then merged by one gated pull request, the
  plan item `Done` with a closeout equal to `LedgerReport`'s rendering of the throwaway ledger's rows.
  P4b-AT5 and P4b-AT6 (sc-25989, `SlotAcceptanceTests`; a machine set up with `--slots 2` and `Worker__Slots=2`): two items of
  one `factory work` in a worker session at the same time, each in its own slot (overlapping `worker_sessions` rows), both merged
  (`FACTORY_E2E_P4B_SLOT_STORIES` + the watch scope); and two `factory run` children in different slots, the first SIGKILLed
  (its own pid only) during Implement and started again — the same session in the same slot, the other item's session,
  worktree and worker untouched (`FACTORY_E2E_P4B_AFFINITY_STORIES`).
  P4b-AT3 (sc-25983, `KanbanAcceptanceTests.P4b_AT3_…`, `FACTORY_E2E_P4B_KANBAN_PROJECT` + `FACTORY_E2E_P4B_KANBAN_OTHER_TOKEN`,
  a second identity's full-scope token; no model): on a throwaway Backlog item the test creates (once, by its idempotency key),
  in flight only in the throwaway ledger — a second caller's claim on the item the factory holds is refused; one intake poll
  renews the claim past its first (20 s) expiry; a write on a stale version is refused `conflict` and writes nothing; and once
  the second caller holds the item the next poll pauses it (``claim lost: … claimed by `<them>` ``, `parked`), their claim, column
  and version untouched.

## Commands

```sh
docker compose up -d                          # ledger Postgres on localhost:5434
dotnet tool restore
dotnet build
dotnet test                                   # unit tests; acceptance tests skip
dotnet ef database update --project src/DarkFactory.Orchestrator
dotnet ef migrations add <Name> --project src/DarkFactory.Orchestrator -o Ledger/Migrations
dotnet run --project src/DarkFactory.Orchestrator -- run sc-1234             # or gh-12 (a released GitHub issue), kb-7 (a Kanban item)
dotnet run --project src/DarkFactory.Orchestrator -- kanban key <item GUID>   # prints the item's kb-<n> (given now if it has none); only for an item `factory work` has not listed
dotnet run --project src/DarkFactory.Orchestrator -- kanban key --epic <epic GUID>   # prints the epic's key kb-<n> (given now if it has none), so `pause|stop --epic kb-<n>` works before any item of the epic was read
dotnet run --project src/DarkFactory.Orchestrator -- work          # long-running host: dashboard + session hub on 127.0.0.1 + the intake loop (polls every configured work source)
dotnet run --project src/DarkFactory.Orchestrator -- dashboard set-password   # dashboard login (hash → keychain)
dotnet run --project src/DarkFactory.Orchestrator -- pause --factory          # also continue/stop; --epic N (a Shortcut epic) | kb-N (a Kanban epic's key), or --item sc-N | gh-N | kb-N | rc-N, or --repo owner/name
dotnet run --project src/DarkFactory.Orchestrator -- recovery status          # repository hot-file incidents from the ledger only (--page N); never advances recovery
dotnet run --project src/DarkFactory.Orchestrator -- recovery reassess --repo owner/name   # human-only re-arm of an ESCALATED incident: fresh assessment, fresh budgets; clears no control
dotnet run --project src/DarkFactory.Orchestrator -- continue --freeze        # clear the automatic freeze (also --usage)
dotnet run --project src/DarkFactory.Orchestrator -- github-app setup
dotnet run --project src/DarkFactory.Orchestrator -- github-app setup --gate   # the merge gate's own App (then re-run protect)
dotnet run --project src/DarkFactory.Orchestrator -- github-repo protect owner/name   # rulesets; owner's GH_TOKEN / `gh auth token`
sudo scripts/setup-worker-user.sh             # the owner, once and after any change to scripts/factory-worker-launch: the sandbox user and its helper
sudo scripts/setup-worker-user.sh --slots 2   # the same for 2 worker slots (_factory, _factory2), then Worker__Slots=2
```

## Configuration

Env vars use `__` as the section separator (`Router__BaseUrl`). Secrets are never
committed: they come from env/user-secrets or the macOS login keychain
(service `dark-factory`).

| Key | Default / source |
| --- | --- |
| `Router:BaseUrl` | `http://localhost:8080` (an absolute http(s) URL; one naming a model provider's host or key — `src/DarkFactory.Analyzers/ProviderMarkers.txt`, the gateway lint's list, embedded in both the analyzer and the orchestrator, `Gateway/ProviderMarkers.cs` — is refused, P1-E1) |
| `Router:Key` | env `FACTORY_ROUTER_KEY`, or keychain account `router-key` |
| `CodeGraph:BaseUrl` | the hosted CodeGraph (`OutboundHttp.CodeGraphDefaultBase`; an absolute http(s) URL naming no model provider): reviewers' CodeGraph tools (`ReviewTools.CodeGraphTools`) go to its `mcp` endpoint |
| `CodeGraph:OverlayTimeoutMinutes` | `10` (> 0: how long a review waits, before its first panel call, for CodeGraph's overlay of the PR head; not ready by then, reviewers' CodeGraph calls read the default-branch index, labelled not the PR head; sc-25708) |
| `CodeGraph:Token` | env `FACTORY_CODEGRAPH_TOKEN`, or keychain account `codegraph-token`; owner-side only (E5). Optional: without it the factory starts and every CodeGraph tool answers the reviewer an error result (recorded), triage gets no CodeGraph, and the merge gate's `risk-threshold` fails wherever a tier requires it (sc-25709), so no normal or protected change merges (`factory run` and `factory work` warn at start-up) |
| `Kanban:McpUrl` | `https://kanban-mcp.trefry.net/mcp` (`OutboundHttp.KanbanDefaultMcpUrl`; an absolute http(s) URL naming no model provider; MCP Streamable HTTP): the Kanban upstream (sc-25707), offered to reviewers through their tool loop and to a plan item's planner session through the loopback proxy (`McpProfile.Planner`, sc-25870); never to triage sessions, whose proxy is started without it (`McpProfile.Triage`: the issue text is untrusted and the triage's answer is posted on the issue) |
| `Kanban:Token` | env `FACTORY_KANBAN_TOKEN`, or keychain account `kanban-token`: a KanbanBoard PAT (`Authorization: Bearer`), owner-side only (E5). Optional: without it there is no Kanban upstream (not an error) |
| `Kanban:WriteToken` | env `FACTORY_KANBAN_WRITE_TOKEN`, or keychain account `kanban-write-token` (sc-25981): the orchestrator's own KanbanBoard PAT, scope `full`, with which the Kanban work source claims, moves, comments on and links items. Owner-side only and apart from `Kanban:Token` (E2): equal to it is refused at start-up (`WorkSourceRegistry.EnsureKanbanTokensApart`, from `FactoryOptions.ValidateSettings` and again where the source is built; exit 2, naming the keys and neither token, sc-25992 — the read token reaches model sessions through the MCP proxy); it is read in one place (`WorkSourceRegistry.Kanban`), held by one client (`Gateway/KanbanWrites`) and is in no MCP upstream, profile, allowlist or proxy grant. Needed only to work a Kanban item (`factory run kb-N`, `factory kanban key`): missing, those print the reason and exit 2 |
| `Kanban:Watch:Projects` | empty = watch nothing; comma-separated KanbanBoard project keys (each ≤ 24 characters, no spaces; upper-cased, once each; else refused at start-up naming the key). An item is in scope when its project is one of them. Set, it makes KanbanBoard a configured work source that `factory work` polls (sc-25982) and then needs `Kanban:WriteToken` (else refused at start-up); each key is checked on the board at start-up |
| `Kanban:WebUrl` | `https://kanban.trefry.net/` (`OutboundHttp.KanbanDefaultWebUrl`; an absolute http(s) URL without credentials, query or fragment, naming no model provider): what a Kanban item's, epic's and document's link is built on (`KanbanLinks`: `?project=<id>[&epic=<id>]&item=<id>`) — KanbanBoard's MCP returns no URL, and nothing is requested from this address |
| `Shortcut:ApiToken` | env `SHORTCUT_API_TOKEN`, or keychain account `shortcut-api-token`. Needed only to work Shortcut stories (sc-25982): with no Shortcut watch scope the factory starts without it, on its other work sources. With the token and no watch scope, `factory work` picks up no Shortcut story but still finishes the Shortcut items already in its ledger (a lane of its own); without the token it cannot, and warns at start-up when the ledger holds active ones |
| `GitHub:AppId`, `GitHub:PrivateKeyPem` | keychain `github-app-id`, `github-app-private-key` (written by `factory github-app setup`) |
| `GitHub:Gate:AppId`, `GitHub:Gate:PrivateKeyPem` | keychain `github-gate-app-id`, `github-gate-app-private-key` (written by `factory github-app setup --gate`): the merge gate's App, the only credential that merges |
| `Review:Models`, `Review:Correctness:Models`, `Review:SpecConformance:Models`, `Review:Security:Models`, `Review:Confirm:Models` | retired (sc-25626, epic E8): no reviewer or second-opinion model is pinned — every panel call names the router's `high` model class. Any of them set (even empty) is refused at start-up (`FactoryOptions.RejectReviewModelSettings`, `ReviewConfigurationException`: `factory run` and `factory work` print the reason and exit 2, before any router call) rather than silently ignored |
| `Review:TimeoutMinutes` | `10` (> 0: one reviewer call, its whole answer stream included) |
| `Gate:CiPollSeconds`, `Gate:CiTimeoutMinutes` | `30`, `30` (each > 0: CI on the PR head is polled until it finishes; still running at the timeout escalates) |
| `Gate:TestTimeoutMinutes` | `20` (> 0: one sandboxed run — restore, build, the new tests — of the `new-tests-fail-on-base` check; still running at the timeout fails the check) |
| `Factory:DefaultRepo` | `michaeltrefry/dark-factory-sandbox` (a story line `Repo: owner/name` overrides) |
| `Factory:Repos` | empty; comma-separated `owner/name` (each well-formed, else refused at start-up naming the key): repositories the factory may plan and work in besides the default one (sc-25877). It only adds to the configured repositories (`FactoryOptions.ConfiguredRepos` = `Factory:DefaultRepo` ∪ `Factory:Repos` ∪ `GitHub:Watch:Repos`, case-insensitive, each once): a plan may name them and the freeze's main-red watches them. It turns on NO issue polling and works with `Worker:RunAs=none`. Both GitHub Apps must be installed there |
| `Shortcut:Watch:Teams`, `Shortcut:Watch:Epics` | empty = watch nothing; comma-separated team mention names/ids, epic ids. Set, they make Shortcut a configured work source that `factory work` polls and then need `Shortcut:ApiToken` (else refused at start-up). `factory work` needs at least one configured (watched) work source — this, `Kanban:Watch:Projects` or `GitHub:Watch:Repos` — and exits 2 naming all three otherwise (sc-25982; a deliberate change from `main`, where a Shortcut token with an empty scope started the host with a warning and nothing to pick up; owner decision, confirmed 2026-10-14: kept) |
| `GitHub:Watch:Repos` | empty = triage no issues; comma-separated `owner/name` whose issues `factory work` polls and triages (the workers' App needs Issues read/write there; refused with `Worker:RunAs=none`). It is the issue-triage watch list, not the list of repositories the factory may work in: a repository named here is also a configured repository, but to let the factory plan and work in a repository WITHOUT triaging its issues name it in `Factory:Repos` |
| `Intake:PollSeconds` | `60` (> 0) |
| `Intake:MaxItemFailures` | `3` (runs of one item failing in a row before `factory work` escalates/parks it, E10) |
| `Intake:MaxConcurrentRuns` | `1` (a whole number 1–8, `IntakeOptions.MaxConcurrentRunsLimit`: how many items `factory work` runs at once; 1 runs them one after another. Runs overlap outside the worker phases — "Worker phases" below; sc-25988 — and, with `Worker:Slots` above 1, inside them too, one per slot) |
| `Usage:PollSeconds` | `60` (> 0: `factory work` reads the router's subscription usage; also read at each intake poll) |
| `Freeze:MaxConsecutiveFailures` | `3` (≥ 1: distinct items escalated in a row, no factory merge between, that freeze the factory) |
| `Freeze:HotFileMerges`, `Freeze:HotFileWindowHours` | `8` (≥ 2), `24` (> 0; inclusive at the window's edge): distinct factory merges changing one normalized file of one repository and exact base branch within the window that open a per-repository hot-file incident (epic sc-26335, "Hot-file recovery" under the invariants): that repository alone is held and assessed autonomously; merges into a `factory/changeset-*` branch do not count (the changeset's delivery merge does). The global `hot-file` freeze is written only by a factory built without recovery (`FactoryFreeze` with no `HotFileRecovery`); a legacy global hot-file freeze row is left for a human's `continue --freeze` |
| `Freeze:CostRisingRounds` | `2` (≥ 1): consecutive rises of an item's cost per round (implement, then each fix round) that freeze it |
| `Freeze:CheckFailedEvaluations`, `Freeze:CheckFailedMinutes` | `10` (≥ 1), `30` (> 0): a freeze check failing that many evaluations in a row (counted per process, `FreezeCheckFailures.Process`), or for that long, is written as a `check-failed` freeze |
| `Controls:MaxReadFailures` | `10` (≥ 1): failed reads in a row of an item's controls while its worker or the gate's test runs go on; the next counts as a Pause (`controls-unreadable`) |
| `Router:CostSettleSeconds` | `5` (after a session's cost is first recorded, it is read once more this much later and the later value kept) |
| `Factory:WorkRoot` | `/opt/dark-factory/work` (clones + worktrees; `~/.dark-factory` when `Worker:RunAs=none`) |
| `ConnectionStrings:Ledger` | `Host=localhost;Port=5434;Database=factory;Username=factory;Password=factory` |
| `Worker:ClaudePath` | `claude` (as the worker user sees it: `~_factory/.local/bin` is first on its PATH) |
| `Worker:RunAs` | `_factory` (the sandbox user, slot 1's; slot n's is this name with n appended: `_factory2`, …); `none` runs workers as the owner (development only) |
| `Worker:LaunchHelper` | `/usr/local/libexec/dark-factory/factory-worker-launch` (slot 1's installed helper; slot n's is this path with `-n` appended) |
| `Worker:Slots` | `1` (a whole number 1–8, `WorkerSlots.MaxSlots`; sc-25989): how many sandbox users run workers side by side — worker sessions at a time. 1 is the one user `_factory`, one helper, one lock file, as before slots. Each slot above 1 needs its user and helper set up first (`sudo scripts/setup-worker-user.sh --slots n`), or start-up fails naming the slot (a slot above the count setup was last run with has no installed helper: setup removes the higher slots' helpers). Independent of `Intake:MaxConcurrentRuns`: more runs than slots is the ordinary case (runs also overlap outside worker phases); more slots than runs is allowed (the others serve a `factory run` beside `factory work`; `factory work` says so at start-up). Refused, not ignored, with `Worker:RunAs=none` unless 1 ("Worker slots" below) |
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

Worker sandbox (E5): workers run as a hidden sandbox user — `_factory`, or with several worker slots (`Worker:Slots`, sc-25989;
"Worker slots" under the invariants) one of `_factory`, `_factory2`, … — via
`sudo -n -u _factory factory-worker-launch claude …` (one NOPASSWD sudoers rule per sandbox user: that user's own copy of the
helper, as that user only). Everything in this paragraph holds per sandbox user.
The owner's home is ACL-denied to it, so `~/.ssh`, `~/.config/gh` and the login keychain are
unreachable. Router variables go to the helper on stdin; the helper builds the worker env from scratch
(PATH, HOME from the password database, TMPDIR the user's own `getconf DARWIN_USER_TEMP_DIR` (else a 0700 `~/.tmp`; either only if
it is a real directory, not a symlink, owned by the running user — an unusable getconf answer falls back to `~/.tmp`, an unusable
`~/.tmp` makes the helper refuse to run, exit 70) — never the shared `/tmp`,
where one sandbox user's NuGet locks refuse another's restore, helper_version 5, sc-25992. Residual, accepted: a worker session
can replace its own user's `~/.tmp` with a symlink (on macOS that bites only once it has also replaced its own `getconf
DARWIN_USER_TEMP_DIR` directory, `…/T`, with one: the helper falls back from that to `~/.tmp`); the helper then refuses that
slot's later runs with exit 70 until someone removes it — it fails closed and concerns only that user's own files, the same class
as `~/.cargo`. Recover by removing the symlink(s) as root or as that sandbox user (the helper makes `~/.tmp` again, 0700; macOS
recreates its `…/T` directory) — MSBuild node reuse/build server off,
router vars), refuses any
other variable or a program that isn't an absolute path/plain name, and when the worker exits or its stdin
closes kills the tree, the process group and then every other process of its own user (a sweep of `ps -U "$self_uid"`, the
helper's own uid and no other, that spares the helper's own pid and its descendants — never `kill -1`, which on macOS kills the
sender too), then exits with the worker's status. The sweep runs only when the helper's user name is `sandbox_user` and its uid is
`sandbox_uid`, which setup pins in each installed copy to that copy's own user (the uid must be in 400-499, the range setup
allocates, still `sandbox_user`'s uid, and not the sudo caller's); otherwise the helper prints `refusing the _factory uid sweep:
<why>` and skips it — so a copy run as another slot's user sweeps nothing, and one pinned to another slot's uid refuses.
`send_signal` never signals pid or group 0 or 1 or an empty target. So each sandbox user is
single-tenant: whatever runs through its helper holds that slot's lock (`WorkerLock`: `<work root>/.factory-run.lock` for slot 1,
`slot-locks/.factory-run-n.lock` for slot n, a directory closed to every sandbox user) for that phase — one worker phase per sandbox user at a time, not one run ("Worker phases",
sc-25988, under the invariants), and one per slot side by side. Between slots the sandbox users are isolated for writing, not
for reading ("Slot isolation", sc-25990, under the invariants): a worktree is written by its own slot's user and the owner
only (checked before every push of worker work), each slot's MCP session directory is read by its own user and the owner only,
and every slot's user reads everything else under the work root — other slots' worktrees included — through the work root's
inherited read entry (accepted). Setup, the owner's, once and again after any change to
`scripts/factory-worker-launch`: `sudo scripts/setup-worker-user.sh [--slots n]` (n = `Worker:Slots`, 1–8, default 1). Per
slot it creates the hidden role account (slot 1 `_factory`, slot n `_factoryn`; own group; a uid of its own in 400-499 — setup
refuses one outside the range, and two slots sharing one, before installing anything; home `/Users/<user>`, mode 0700, so no
sandbox user enters another's), denies it the owner's home, gives it the work root's inherited read entry, installs its own
Claude Code (`~<user>/.local/bin`), and installs ONE copy of the helper for it — `factory-worker-launch` for slot 1, at the path
it always had, `factory-worker-launch-n` for slot n — with `sandbox_user` and `sandbox_uid` set to that user, and its sudoers
line. A re-run with a smaller n leaves the higher slots' accounts (and homes) in place, drops their sudoers lines and
removes their installed helpers — `rm -f --` of exactly the fixed paths `factory-worker-launch-<m>` for m = n+1…8 in the
root-owned helper directory, each checked against the path setup itself derives; never slot 1's helper, a glob or a directory —
and prints which of those users still exist (`No longer set up (more slots than --slots n): _factory2 …`). So a factory whose
`Worker:Slots` is still above n fails at start-up (`Worker slot m (…): … cannot read the launch helper …`), not in its first
worker phase with sudo's "a password is required". The readiness check has
two parts (sc-25988), because a run of a helper kills every process of its user and so belongs under that slot's lock.
What start-up checks, running nothing and taking no lock (`WorkerSlots.EnsureHelpersCurrentAsync` →
`WorkerSandbox.EnsureHelperCurrentAsync` per slot): EVERY slot's installed helper is
read and must have `helper_version` ≥ `WorkerSandbox.HelperVersion` (5), name its own slot's user as `sandbox_user`, pin a uid,
and, with those two lines undone, have the SHA-256 of the
repo's helper (compiled into the orchestrator as a resource) — else `Stale helper: … re-run \`sudo scripts/setup-worker-user.sh\``
(or `cannot read the launch helper …`), led with several slots by the slot that failed (`Worker slot 2 (_factory2): Stale
helper: …`) and naming the command with the slot count (`sudo scripts/setup-worker-user.sh --slots 2`:
`WorkerSlots.ReinstallCommand`). `factory work` and `factory run` refuse to start on it (`FactoryRunner.CheckHelperAsync`,
in `Program`: the reason, exit 2, before the router check and before the first poll), and every run checks it again first of all
(`FactoryRunner.RunAsync`, a `FactoryUnavailableException` before the ledger is opened or the board asked: nothing is claimed
or recorded, and the intake loop ends the poll). What is deferred to a run's first worker phase, and to each triage, under the
lock of the slot it holds (`WorkerSlots.EnsureReadyAsync` → that slot's `EnsureReadyAsync`, which starts with the same read; so
each slot is probed before a worker runs in it, by the runs that land there, and a failure names the slot): the probe with the
real router variable names
(allowlist `ANTHROPIC_BASE_URL`, `ANTHROPIC_CUSTOM_HEADERS`, `ANTHROPIC_AUTH_TOKEN`; `ANTHROPIC_CUSTOM_HEADERS` may come on several
stdin lines, one header each, which the helper joins with line breaks — since helper_version 3, sc-25659), which fails if the
helper refuses a variable, the user is not usable or the probe's stderr has a uid-sweep refusal; and last `<Worker:ClaudePath>
--version` as that slot's user through its helper, failing with
`Upgrade _factory's claude: …` (and the `claude update` command) below `WorkerSandbox.MinClaudeVersion` (2.1.291, the release
the triage confinement — `--setting-sources ""`, `blockReadsOutsideWorkingDirectories`, `dontAsk` — was checked against; an
older CLI may silently ignore a setting it does not know). Each sandbox user has its own PATH head (`~<user>/.local/bin`, its
own Claude Code) and the helper's fixed tail (`/usr/local/share/dotnet`, `/opt/homebrew/bin`, `/usr/local/bin`, …), so dotnet,
git, cargo and rustc are the machine's for every slot, while `~<user>/.claude`, `~<user>/.cargo` and the NuGet cache are per
user. Setup checks dotnet, git and claude per user through that user's helper, and reports, without failing, whether `cargo` and
`rustc` answer it (Rust target repos only: the new-tests check of a Rust pull request is an `error` without
them, sc-25875). Every normal (re)run kills every process of every sandbox user it sets up
(its toolchain check runs through each helper): close their sessions and stop `factory work` first.
`--remove-worker-login` (opt-in) is a standalone mode: it runs none of the setup (no helper, no toolchain check, no
installs) and only deletes each sandbox user's own Claude login (`~/.claude/.credentials.json`, keychain item
`Claude Code-credentials`; with `--slots n`, of each of the n users), as that user, signalling nothing. The live probe tests
(`LiveWorkerSandboxTests`) run only with `FACTORY_SANDBOX_LIVE=1`, since they kill every process of every configured sandbox
user (including an interactive `_factory` login); they probe every configured slot (`Worker:Slots`, e.g. `Worker__Slots=2`),
each slot's `TMPDIR` checked to be its own user's (sc-25992).

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
  `freeze-paused` (`RunPipeline.FreezePaused`, the automatic freeze), `controls-unreadable` (`RunPipeline.ControlsUnreadablePaused`)
  or `recovery-held` (`RunPipeline.RecoveryHeld`, only an automatic repository hot-file incident held it) auto-resume (`RunPipeline.InFlightAsync`), all but the first only once no control pauses the item and the controls can be read
  (the usage pause lifts at its `ResumeAt`, the freeze at a human's Continue); a parked item never does. The same pauses keep a
  queued item's merge-queue place (`MergeQueue.Member`). `InFlightAsync` lists the items being stopped first. Resumes re-check
  the scope.
- Work sources are named by `ItemNaming` (`sc-<story>`, `gh-<key>`, `kb-<key>`, and `rc-<n>` — the internal recovery source's
  correction items, backed by the ledger's `recovery_corrections`, no board or credential, untrusted like a planner's text, never
  planning: "Hot-file recovery" below —: the ledger `Source`, external id, `factory/<id>` branch,
  `factory-<id>` worktree, `item:<id>` control scope); `factory run`/`--item` take any of them. The intake loop lists every source
  in turn (`IntakeLane`: a lane's preparation, the issue triage, runs before any item the poll starts), then starts the poll's
  items into the run capacity left by runs still in flight from earlier polls, up to `Intake:MaxConcurrentRuns` at once (a poll
  does not wait for its runs, sc-25992: "Runs across polls" under "Intake failures"); a triage and the items' worker phases share each sandbox user (worker slot) one at a time through its lock
  ("Worker phases"). The merge queue is per repo across sources.
- Several work sources (Phase 4b, sc-25982; `WorkSources/WorkSourceRegistry.cs`, E1): every place that needs a source takes it from
  ONE registry keyed by `ItemNaming` — `WorkSourceRegistry.Kinds`, one `WorkSourceKind` per `ItemNaming.All` entry (Shortcut,
  GitHub issues, KanbanBoard, the order the lanes are polled in), each saying which settings configure it and how it is built
  (its own `OutboundHttp` client, owned and disposed by the registry; a source is built once, on first use). It is the only file
  outside `Shortcut/` that names `ShortcutWorkSource` (or the Shortcut namespace, bar `FactoryOptions` for the watch scope's
  type): `WorkSourceRegistryTests.Only_the_registry_names_…` scans the source tree. Two questions, answered from the settings:
  (1) `Configured` — the sources `factory work` polls: those whose watch scope is set (`Shortcut:Watch:Teams`/`Epics`;
  `Kanban:Watch:Projects`; `GitHub:Watch:Repos`). A watched source whose credential is missing is refused
  (`MissingCredentialException`), never left out. The factory starts with any non-empty set, KanbanBoard alone included, and with
  no Kanban setting behaves as before — with ONE deliberate exception, below: no watched source at all is now exit 2. (2) `Of(naming)` — the source of one item named by id (`factory run`, `factory stop
  --item`, an item's ledger row): it needs only that source's credential, no watch scope (`factory run sc-12 --ignore-scope`
  with an empty scope works as it always did; the claim's own scope check is unchanged); without the credential it throws naming
  that credential alone (`WorkSourceKind.Credential`, `WorkSourceRegistry.NeedsCredential`: no watch scope is asked for, since
  none is needed), and when no source is configured at all it adds those of every source (`WorkSourceRegistry.NoneConfigured`,
  built from each kind's `Settings`: credential and watch scope).
  `Find` is `Of` or null. A third, wider set is the intake loop's lanes (`Lanes`, below): watched OR has its credential.
  Wiring, each through the registry:
  `factory work` start-up (`FactoryRunner.CheckWorkStartAsync`, called by `Program`): settings, the credentials every run needs
  (router key, both GitHub Apps, dashboard login — no longer the Shortcut token), then the sources — none watched → exit 2 with
  `WorkSourceRegistry.NothingWatched`: `NoneConfigured` (every source's credential and watch scope) plus how work already in
  flight is finished meanwhile, by id (`factory stop --item <id>`, `factory run <id> --ignore-scope`: both need the source's
  credential and no watch scope). A token with no watch scope is not a configured source: one `stderr` note per such source, none
  for a source no setting names. This is a DELIBERATE CHANGE FROM `main`, stated plainly: there, a Shortcut token with an empty
  watch scope started `factory work` with a warning and nothing to pick up (and it went on finishing in-flight stories); now
  `factory work` exits 2 unless at least one source is watched, as the story's acceptance says — so an owner who empties every
  watch scope to drain the factory must keep one scope set (the lanes below then finish every source's in-flight items) or finish
  the leftovers by id (owner decision, confirmed 2026-10-14: kept). Then each configured source's `ValidateScopeAsync` on its board (`CheckWatchScopeAsync`; an unwatched
  source's scope is not checked: no board request). The host (`AddIntake`) registers the registry as a singleton; the intake
  loop takes `registry.Lanes(…)` — one `IntakeLane` per source that is watched OR has its credential (coordinator decision,
  sc-25982 review), each with a `FactoryItemRunner` of its own, whose `InFlightAsync` lists only that source's items
  (`RunPipeline.InFlightAsync(ledger, naming, …)`: the naming is required, there is no default source). Why wider than
  `Configured`: the ledger may hold items of a source whose watch scope was emptied since (or, for Shortcut, never set — `main`
  always had a Shortcut lane), and without a lane no poll would finish their pending Stop, retry a failed closeout from Watch,
  evaluate a `Planned` plan item or resume a pause. An unwatched source's lane picks up nothing new and asks its board nothing
  for it: each source's `ListReadyAsync` answers an empty scope with no board request (`ShortcutWorkSource` and `KanbanWorkSource`
  return at once; `GitHubIssueWorkSource` reads only the ledger and lists items of watched repositories), nothing is claimed, and
  the lane has no `Prepare` — only a WATCHED GitHub issues lane triages (`Prepare`), so no issue is triaged for an issues source
  that watches no repository (its lane exists whenever the workers' GitHub App is configured, i.e. always in `factory work`).
  With any number of lanes the runs overlap only as `Intake:MaxConcurrentRuns` allows, and in the sandbox only one worker phase per worker slot ("Worker phases", "Worker slots"). A source WITHOUT its credential gets no lane: after the ledger
  migration `factory work` prints one `warning:` per such source whose ledger still holds active (not Done, not Cancelled)
  items (`WorkSourceRegistry.UnreachableAsync`: the source, the count, the credential that brings it back) — those items
  cannot be finished, stopped or resumed until then, and the dashboard still lists them. `factory run <id>`
  (`Program` → `RunCommandAsync(requireSource: …)`): the item's source is resolved before the router is asked; not configured →
  exit 2. `factory pause|continue --item <id>` only write the control (any id of `ItemNaming.All`); `factory stop`
  (`FactoryRunner.ControlAsync`, `FactoryItemStops`) stops each item on the source its id names — for one item that source is
  resolved before anything is written, on both paths: the CLI exits 2 when it has no credential, and `ControlActions.StopAsync`
  itself (so the dashboard's Stop too) answers not Ok, `<id>: NOT stopped, and nothing was written. …`, before the Stopping
  control is set (a Stopping item with no lane could never be finished, and a Stopping scope cannot be continued). In a wider
  scope (the factory, an epic) each item's id comes from its ledger row (`Source` + `ExternalId`) and an item whose source has
  no credential is reported `stop NOT finished (…); it stays Stopping, and nothing finishes the stop until that credential is
  set` — never "the next poll retries it", which holds only for other failures — and keeps its Stopping control. A plan's Stop
  cascade runs on the plan item's own source (its stories are that source's). The dashboard's controls use the same registry.
  An invalid scope's message names every source's item scope (`item:sc-<id>, item:gh-<key> or item:kb-<n>`).
  Tests (`WorkSourceRegistryTests`) build their options from explicit settings with no keychain and no process environment
  (`FactoryOptions`' `environment` parameter, tests only), so none is skipped on a machine whose shell carries a source's token. `IItemStops` stops one item with `StopAsync(ItemRef)` and drafts a retained coordinator's selected-repository changesets with `DraftRepositoryAsync`. Epic scopes
  carry their source (sc-25985, under Controls below): `--epic N` is a Shortcut epic, `--epic kb-N` a Kanban epic, each reaching
  only its own source's items, and an item whose epic the ledger lacks is looked up
  on its own board (`ControlActions`' `sources`), one not configured being reported as unknown.
- KanbanBoard items (Phase 4b, sc-25981, sc-25982; `Kanban/`): a third work source behind `IWorkSource`. Polled by `factory work`
  when `Kanban:Watch:Projects` is set (the registry above), run by `factory run kb-<n>`, and paused, continued and stopped by id
  like any item (Stop: pull request to draft, item back to `Backlog` with a comment, the factory's claim released, Cancelled).
  The intake loop's claim renewal (sc-25983, below) asks every lane the registry builds — a watched source's and the lane of a
  source that only has its credential alike (`WorkSourceRegistry.Lanes`) — so a Kanban item in flight keeps its claim after
  `Kanban:Watch:Projects` is cleared, for as long as the write token is set; with no token there is no lane, nothing renews it,
  and the start-up warning says so. Plans: "Plans on Kanban" below (sc-25984). `factory kanban key <GUID>` stays: the intake loop gives every item it LISTS its key, but
  `factory run kb-<n>` on an item no poll has listed — `factory work` not running, the item outside the watched projects (run
  with `--ignore-scope`), or not `Ready` yet — still needs the key from somewhere. Contract: KanbanBoard's
  `docs/mcp-contract.md` (tool and argument names are compared exactly there, and an argument a tool does not have is refused).
  Identity (E4): the board names an item by a GUID; the ledger gives it a key once, in `kanban_items` (migration `KanbanItems`;
  `KanbanKeys`: unique index on the GUID, the loser of a race reads the winner's key; a save that failed and left no row rethrows
  its own error), before anything else is written for it —
  when `ListReadyAsync` first lists it or `factory kanban key <GUID>` asks (which reads the item from the board first: an unknown
  id gets no key). Nothing written onto the board derives from that key: the claim marker is `dark-factory:<item GUID>`
  (`KanbanWorkSource.Marker`) and a comment's idempotency key the SHA-256 of the item's GUID, its `version` and the body
  (`CommentKey`: a retry of the same comment adds nothing; the same words after the item was moved or edited are a new comment).
  Both halves are limits, accepted, because `version` moves only on an update or a move (a comment, a link and a claim leave it):
  the same words said again on purpose at an unchanged version are dropped by the board (it answers the earlier comment) while the
  pipeline records them as posted, and a retry of one comment after the version moved in between posts it twice. The key is not
  changed for either: a key written onto the board derives from board ids, never from a ledger key or a counter of the factory's.
  Writes (E2): only the orchestrator writes the board, through its MCP, with its own full-scope token (`Kanban:WriteToken`) in the
  one client `Gateway/KanbanWrites.Client` builds for the typed caller `KanbanBoard` — which is no `McpUpstream`, so it is in no
  upstream registry, allowlist, profile or proxy grant; model sessions keep the read token and `McpServers.KanbanTools` (read tools
  only). `KanbanWriteIsolationTests` hold each half: no contract write tool on an allowlist or in a profile, the upstream sending
  the read token and no write tool, a planner grant of the real proxy refusing each of the adapter's write tools with nothing
  forwarded, and a source scan (the token read, the client and the caller built only in `WorkSources/WorkSourceRegistry.cs`; nothing
  under `Mcp/`, `Gate/`, `Plans/`, `Issues/`, `Worker/`, `CodeGraph/`, `Sessions/`, `Dashboard/` naming them). Every write is
  conditional or idempotent (E3): a status change is `update_work_item(itemId, status, expected_version)` on the version just read
  (it puts the item at the end of the column and changes nothing else), a comment carries its `idempotency_key`, and claim, renew, release
  and link are idempotent on the board (`add_work_item_link` of a kind and URL the item carries changes nothing).
  Ready = `list_work_items(projectId, status: Ready, unblocked_only, claim_state: unclaimed, limit, include_comments: false)` per
  watched project, paged by cursor (at most `KanbanWorkSource.MaxPages`, 50, pages of `KanbanWorkSource.DefaultPageSize`, 50 — the
  contract's default, not the board's maximum of 200, `KanbanBoard.MaxPageSize`: `McpHttpClient` refuses an answer over 1 MiB,
  `MaxAnswerBytes`; so at most 2,500 ready items of one project, more is refused), each listed item
  checked again (project, status, no claim, no open blocker). `ClaimAsync` re-reads the item and refuses, writing nothing: a key
  the ledger never gave out, an item gone from the board, a project outside `Kanban:Watch:Projects` (skipped by
  `--ignore-scope`), another caller's claim (``the item is claimed by `<claimant>` ``: a claimant's name and a marker are another
  caller's text, so wherever a refusal or a lost claim names them each is cut at 200 characters and made one inert code span,
  `UntrustedText.CodeSpan`), a status that is not `Ready` (or `InProgress`
  and the factory's, next), and an open blocker (only `Done` finishes one; a `Closed` blocker still blocks). An `InProgress` item
  is the factory's when it carries the factory's live marker, or — a lapsed claim — when it carries no claim at all and the ledger
  records that this factory claimed it (`KanbanKeys.ClaimedAsync`: a `claimed` checkpoint on `kb-<n>`, at any time): it is then
  claimed again and read back like a fresh claim, so the escalation remedy `factory run kb-<n>` works more than a day later. An
  `InProgress` item with no claim that the ledger does not record as claimed (a run that died between the board's claim and its
  `claimed` checkpoint more than a day ago, or a person's work) is refused, nothing written (`KanbanWorkSource.LapsedRefusal`:
  "… the factory's claim may have lapsed): move it back to Ready to hand it to the factory again"); one under another marker is
  always refused. Residual, accepted: once a claim has lapsed the board shows no one on the item, so a person who took an
  escalated item over without claiming it or moving it loses it to a later `factory run kb-<n>` of it. Then
  `claim_work_item(id, marker, ttl_seconds)` — `already_claimed` (another caller won the race, or holds the same marker under
  another identity) is a refusal too, naming the claimant (the item is read once more for the name) —, the claim read back
  (`claim_marker` and `claimant_id`), and the move to `InProgress`; an item someone moved away meanwhile gets the claim released
  again and a refusal, and a move that keeps conflicting past `MaxConflictRereads` gets the claim released again before the
  conflict is thrown (a claim left on a still-`Ready` item would hide it from every ready listing for a day). `ReportStateAsync`:
  Claimed → `InProgress`, Merged → `Done`, Stopped → `Backlog` with the factory's claim released and the comment posted; an item
  already in the target column is not written. `LinkAsync` attaches each URL under its kind from the URL (`…/pull/<n>` `pr`,
  `…/tree/<branch>` `branch`, else `other`).
  Plans on Kanban (sc-25984; `KanbanWorkSource`'s plan members, the pipeline unchanged in kind: it reaches the board only through
  `IWorkSource`, E1). A Kanban item carrying the label `plan` — the board's own labels, read from `label_list` — is a plan item
  (`PlansItems` true: the board is the owner's, `ItemNaming.Trusted`), planned, released and delivered exactly as "Plan items",
  "Dependency-aware release", "Plan failures and controls" and "Changeset branch" say for a Shortcut story; what differs is only
  how the board is written. `CreateChildrenAsync`: each planned item is ONE `create_work_item` in the plan item's project and —
  when it has one — its epic (no epic: the project alone; nothing is refused, since the items are found again by key, not by
  where they are), status left to the board's default `Backlog`, type by `KanbanWorkSource.ItemType` (the planner's `feature`,
  `bug`, `chore` → the board's `Story`, `Issue`, `Task`; `StoryType` is the way back, so a planned item reads back with the type
  it was planned as; any other type is refused before any write), under the idempotency key `KanbanWorkSource.ChildKey` =
  `ChildItem.Key(<plan item's GUID>)`: `dark-factory:child:<GUID>:<SHA-256 of the name and the description as sent>`, 120
  characters (the board takes 128 printable ASCII ones, per identity and project). The board creates once per key and answers
  the same item on every repeat (`created` false), so a retry after a failure part-way, a restart, or a lost answer (the typed
  caller sends a keyed create again as it is) creates nothing twice and needs no search for what an earlier attempt made — and a
  namesake someone else wrote, having no such key, is never taken for a planned item. Residuals of the board's own key rules,
  accepted: a key belongs to the calling identity, so a retry under ANOTHER identity's `Kanban:WriteToken` would create the
  items a second time (keep the factory's service identity while a plan's items are being created); a repeat answers the item
  "as it is now", so one a person edited, moved or closed since is reused as they left it; and deleting an item frees its key,
  so a retry creates a deleted one again. `import_epic_plan` is NOT used: it always creates a new epic. Identity (E4): the item's GUID is the board's to give, so the board is written first; its ledger key is
  allocated the moment the create answers — with its project, like any Kanban item's (`KeyAsync(item, project)`, sc-25985), and its epic is recorded when it is first read for work (`ReadSpecAsync` → `EpicKeyAsync`: `WorkItem.EpicId` is the plan item's epic's key, so an `epic:kb-<n>` control holds it) — before its dependencies are written, and `plan-stories`, blockers, branches and every
  ledger row name it `kb-<n>` from then on. Everything written ONTO the board names GUIDs instead: the idempotency key, and the
  planned-item marker `<!-- dark-factory:planned-from <plan item's GUID> -->` with the sentence after it (`Plan.Children` is
  given `WorkStory.BoardRef` — the item's id on its board, `WorkStory.BoardId`, which is its ledger name for a source that has no
  other, so a Shortcut child's marker and key are byte-for-byte what they were). Where a person reads the marker's plan item the
  ledger's name stands in (`WorkStory.PlannedFrom`: the Kanban source translates the GUID through `KanbanKeys.KnownAsync`,
  allocating nothing; `PlannedStory.ParentOf` reads ids up to `MaxParentLength`, 36), so the refusal of a planned item whose plan
  has not recorded it says "its plan kb-1 has not finished creating its stories". Dependencies: `add_work_item_dependency(id,
  blocked_by_id)` per blocker (`KanbanBoard.AddDependencyAsync`: no idempotency key and no version — a dependency moves neither —
  so after a lost answer the blocked item is read and one that lists the blocker is not sent it again; the board treats an
  existing dependency as success anyway); one the create's own answer already lists is not sent. Cycles: a child blocked by
  itself or by a later child is refused before any board call (`ArgumentException`; `PlanParser` has already refused a cyclic
  plan); a dependency the board refuses `cycle` — someone made the blocker depend on the item since — fails the step naming both
  items (`KanbanItemException`: "kb-3 could not be made to wait for kb-2 … Remove it on the board and re-run the plan item"), the
  plan item escalates with no `plan-stories` row, and the re-run creates nothing twice. `BlockersAsync`: `get_work_item`'s
  `blockers`, one read, each by its ledger key (allocated now for an item the factory never saw — with no project, `KanbanKeys.KeyAsync(item)`: a blocker is named by its GUID alone, and its project is recorded when the item itself is next listed or read; a project the row has is kept), finished only in `Done` — a
  `Closed` blocker still blocks. `MakeReadyAsync`: `Backlog` → `Ready` on the version read (`MoveAsync`: a `conflict` is decided
  again on a fresh read, their edit kept, their move left standing); `Closed` is the board's archived (`ReadyResult.Archived`,
  reported in the board's words through `IWorkSource.Shelved` — what a shelved item is, how to undo it, and the verb:
  Shortcut `archived`/`unarchive it`/`archive`, Kanban `Closed`/`move it back to Backlog`/`close` — wherever a plan speaks of
  one: the hold "kb-3 is Closed on the board … move it back to Backlog", the waiting log line, a stopped plan's comment ("Taken,
  finished or Closed by someone else since their release") and the no-longer-validates escalation ("finish or close those
  stories by hand"); Shortcut's texts are unchanged); any other
  column has left the backlog. `InBacklogAsync`: the `Backlog` column. `ReturnUnstartedAsync` (a stopped plan's released,
  never-claimed item): `Ready` → `Backlog` on the version read, the comment, and a claim of the factory's own left on it released
  — only while it is still `Ready` and carries no claim but the factory's; one someone claimed, moved, finished or closed is
  theirs, nothing written. A `conflict` on the move is decided again on a fresh read by that same rule — status AND claim, in
  the method's own loop, not `MoveAsync` (which looks at the status alone) — so an item still `Ready` that gained another
  caller's claim since the first read is left alone too. One difference from Shortcut, the board's own rule, accepted: the board's ready listing
  (`unblocked_only`) leaves out an item whose blocker is not `Done`, so a released item is listed once every blocker is `Done`
  on the board — which the factory's merge of a blocker makes it (`BoardState.Merged`). Any blocker the plan counts as merged
  that is not `Done` on the board — `merged-into-changeset` (the item stays Escalated and `InProgress`; that escalation already
  says to move it to `Done`), one a person moved out of `Done` again, one whose move to `Done` the board refused — therefore
  keeps its released dependents out of the listing until a person moves it to `Done`; and `factory run kb-<n>` on such a
  dependent is refused by the source, naming the blocker (``claim refused: the item is blocked by `<title>` (<status>)``, the
  title cut and inert in a code span: a blocker may be a planned item, so its title a planner's text). The
  pipeline's own check is unchanged and comes before the board's: a planned item moved to `Ready` by hand while a blocker is
  unmerged is refused at claim naming the blocker (`claim refused: blocked by kb-2 (not merged): …`), also when someone dragged
  the blocker to `Done` (a story of the plan is satisfied only by the factory's merge). Tests: `KanbanPlanSourceTests`,
  `SourceTrustTests`, `KanbanPlanPipelineTests` (a whole plan over the fake board through the real gate), `KanbanSafeWritesTests`.
  Safe writes (sc-25983, E3; `KanbanBoard`, the typed caller, so every write has them). Typed refusals: a refused call's
  `{code, message}` is a `KanbanException` with the contract's code (`KanbanException.Codes`: `scope_denied`, `not_found`,
  `conflict`, `already_claimed`, `cycle`, `invalid_argument`, `busy`; `Refused` true: it changed nothing — true only for those
  seven: an error object carrying any other code promises nothing and is handled as an error that may have written, below);
  only `busy` is ever sent again. Busy: the same call — the same arguments, so the same idempotency key and expected version — at most
  `KanbanBoard.MaxBusyRetries` (4) more times, after `BusyBackoff` (1 s, 2 s, 4 s, 8 s; the board answers `busy` only after
  waiting 10 s itself), then `busy` is thrown and the run fails that step. Read before repeat: an error that is no typed refusal
  (a plain MCP error without `{code, message}`, one with a code the contract does not name, an HTTP error, a timeout, an answer
  that is not the contract's JSON —
  `MayHaveWritten`; never the caller's own cancellation) may have written, so a write without an idempotency key reads the item
  (`get_work_item`) before it is sent again, and is sent again at most once (`MaxRepeatsAfterLostAnswer`); a read that fails is
  thrown, never a blind repeat. What the read decides, per write: a status change is done when the version moved and the item is
  in the target column (else it is repeated on the same `expected_version`, which the board refuses `conflict` if anyone wrote
  in between); a link when the item carries that kind and URL; a release when the item no longer carries the marker (the board
  would refuse a release that happened); a claim and a renewal are read and then sent again whatever the read shows (the same
  caller's claim is idempotent by the contract, and only its own answer tells this identity's claim from another's under the
  same marker). A write that carries an idempotency key — a comment, and `KanbanBoard.CreateItemAsync` (`create_work_item`,
  key required; the typed caller's create, which a plan's items are made with, sc-25984) — is sent again
  with the same key and no read: the board answers the object the key already made (`comment_created`/`created` false).
  Status changes stay `update_work_item(itemId, status, expected_version)`, not `move_work_item`: a status change has no
  position to ask for, `update_work_item` puts the item at the end of its new column by itself, while `move_work_item` takes an
  `order` (left out: 0, the top of the column, ahead of everything waiting there) and renumbers the column's other items; both
  check `expected_version` alike. Conflicts (`KanbanWorkSource.MoveAsync`): the board's claims are advisory (an update or move
  by anyone ignores them), so a `conflict` on a status change means someone else wrote the item since it was read. It is never
  written over: the item is read again and the move decided again, at most `MaxConflictRereads` (3) times, then the conflict is
  thrown (by `ClaimAsync` after it gave its claim back). Still in the column the move was decided from (they edited something else): the move is made on the new version and
  their edit is kept (only the status is ever sent). In another column (they moved it): their column stands and no move is made
  — `ClaimAsync` gives the claim back and refuses (`the item was moved to <status> while it was being claimed`), and
  `ReportStateAsync` says so on the item (`KanbanWorkSource.NotMoved`, after the caller's comment when there is one; a stop still
  releases the claim). A later report (a re-run) decides afresh on what it then reads.
  Claims (sc-25983): KanbanBoard's own, advisory and expiring. Taken for the maximum lifetime, a day (`ClaimTtlSeconds`), and
  renewed on each intake poll through the optional `IWorkSource.RenewClaimAsync` (default: `Held`, no call — Shortcut's and
  GitHub issues' claims do not expire). Which items (`IItemRunner.RenewableAsync` → `RunPipeline.RenewableAsync`; the
  interface's default is `InFlightAsync`, for a runner with no controls): every item the ledger would resume by itself — in a
  handled state, or Paused from one in a way that resumes by itself (`interrupted`, `user-paused`, `usage-paused`,
  `freeze-paused`, `controls-unreadable`) — WHATEVER the factory, epic, item, usage and freeze controls say now, so a Pause, a
  usage pause or a freeze of any length (a freeze lasts until a human's Continue) costs no item its claim; and (sc-25984) a plan
  item waiting on its stories (`Planned`: no handler, but the factory drives it for as long as its stories take — often longer
  than a claim lives — and then takes its changeset pull request through the gate; a lost claim does not pause it — it has no state to pause from — but holds it: one `plan-held` row and no evaluation while the claim is someone else's, below). NOT renewed: an item
  being stopped (its stop releases the claim), a parked one (a refused claim, a lost claim, a give-up), an Escalated one, a
  merged or terminal one. The controls are read there only to leave out an item being stopped; when
  they cannot be read the item is renewed all the same (the list throws only when the ledger itself cannot be read). The
  renewal list runs nothing: what a poll runs is still `InFlightAsync`'s list (which leaves out what a control pauses), read
  after the renewals. `IntakeLoop.RenewClaimsAsync` asks per lane, whatever the lane's source, before anything is listed ready
  or run. A `ClaimRenewal` is `Held`, `Lost` (why, naming the holder) or `NotRenewed` (why: the board answered with a refusal
  that is the call's or the item's own — the Kanban source: any typed refusal but `busy`, e.g. `scope_denied`, and a key the
  ledger never gave out — logged, and the next item is asked). A renewal that THROWS (the board down or hanging, `busy` past
  its retries, a lost answer: each further call could wait out its whole timeout, inline in the poll) ends that lane's renewals
  for this poll — the rest are tried next poll — and holds up no other lane and no run. The Kanban source sends
  `renew_work_item_claim(id, marker, 86400)`; refused (`conflict`: lapsed or released; `already_claimed`), it reads the item.
  Under another marker it is lost, the reason naming the holder (``the item is claimed by `<claimant>` (marker `<marker>`)``,
  both cut and inert as above; an item gone from the board is lost too). With no claim, or reading as carrying the factory's
  marker, it is claimed again ONLY when it is `InProgress` and the ledger records that this factory claimed it
  (`KanbanKeys.ClaimedAsync`: `ClaimAsync`'s own rule for an unclaimed item; a claim the board then refuses is lost to whoever
  holds it). Otherwise nothing is written and the answer is `Held`: an item in flight at Intake whose run died before its
  `claimed` checkpoint is left to its run's `ClaimAsync` (which still refuses an unclaimed `InProgress` item, `LapsedRefusal` —
  a renewal never takes what `ClaimAsync` would refuse), and an item a person moved out of `InProgress` gets no stray claim that
  would hide it from `claim_state: unclaimed`. A lost claim pauses the item
  (`IItemRunner.ClaimLostAsync` → `RunPipeline.ClaimLostAsync`, under the item's run lock): Paused `claim lost: <reason>` from a
  handled state (an item already paused in a way that resumes by itself stays Paused), one comment on the item saying who holds
  it and what resumes it, then `parked` with the same reason — the comment is posted before the `parked` row, whose Detail ends
  `; comment NOT posted: <error>` when the board would not take it (the item is never listed again, so nothing would post it
  later) — and the dashboard's intake row; the item is left out of that poll's runs. Deliberately not one of the pauses that
  resume by themselves: someone else is working the item, so nothing lists, renews or runs it again until a person's
  `factory run kb-<n>` or `factory stop --item kb-<n>`. That run resumes it only once the item is the factory's again: for an
  item paused for a lost claim (`RunPipeline.ParkedForLostClaim`: its Paused row, or a `parked` row after it, says
  `claim lost`) the run asks `IWorkSource.RenewClaimAsync` before it records `unpaused` (through `IWorkSource`, so for any
  source; the Kanban source takes a released or lapsed claim back by the rule above). `Lost` or `NotRenewed`: the item stays
  Paused, no ledger row and nothing written to the board, and the run's error says who holds it and to run again once the other
  claim is released or has lapsed. `Held`: `unpaused`, and on from where it paused. Typed outcome `deferred`, by the rule for every
  pause and every `parked` row (`StepOutcomes`). Nothing is paused when the item's run is active (its run lock is held — that
  run goes on, claims being advisory, and the next poll finds the claim lost again), when it is being stopped (the stop gives it
  back), or when it is a merged item. A plan item waiting on its stories (`Planned`, sc-25984) is not paused, parked or
  commented on (it has no state to pause from, resumes by itself, and the item is someone else's to write) but HELD: under its
  run lock it gets one `plan-held` row `claim lost: <reason>` (outcome `deferred`; told apart from the other `plan-held` rows —
  a blocker outside the plan, `not evaluated: `, `held by a failed story: ` — by that prefix; not written again while that is
  how its claim is recorded, `RunPipeline.PlanClaimLost`), and `ClaimLostAsync` returns what was done, so the poll leaves the
  plan out of its runs and shows it on the dashboard's intake row; the dashboard's plan row shows the row too. While the claim
  is recorded lost nothing of the plan is evaluated — no story released, no changeset pull request opened or linked — whoever
  runs it: `PlannedAsync` asks `IWorkSource.RenewClaimAsync` first, so a `factory run kb-<n>` by hand answers "is still not the
  factory's to work (claim lost: …); its plan is not evaluated and nothing of it is released" and writes nothing (a different
  holder is a new `plan-held` row). The plan item stays in the renewal list, so it resumes by itself: the first evaluation
  after a renewal that holds (the other claim released or lapsed; the Kanban source takes it back) records
  `plan-claim-regained` (`was claim lost: …`; outcome passed) and goes on; a later loss is a new `plan-held` row. Stories
  released before the loss go on (they are their own items, with their own claims). Residual, accepted: between the other
  caller's claim and the poll that notices it (at most one `Intake:PollSeconds`), an evaluation may still release a story. An item that is not renewed (parked, escalated) loses its
  claim a day after its last renewal, and from then on
  only its `InProgress` column keeps it out of the ready list — the factory's own (`ListReadyAsync` asks for `Ready` only) and
  any other caller's that polls as the contract says; the board shows it unclaimed, so another caller that claims `InProgress`
  items can take it (the residual above). Re-run (`factory run kb-<n>`), its lapsed claim is taken again by `ClaimAsync` (an
  escalated item re-enters Intake) or by the resume's renewal (a claim-lost item); if someone holds it by then, that is a
  refused claim or a claim still lost, as above. A single run that holds the
  intake loop for more than a day (no poll in between), and `factory work` being down that long, also leave a lapsed claim until
  the next poll, whose renewal takes it again if nobody else has. `factory run`
  alone renews nothing: only `factory work` polls.
  Repository: the item's project's `repository_url` (`GitRemoteReads.RepoOf`: the https and ssh forms of a github.com
  repository, with or without `.git`), overridden by a `Repo: owner/name` line in the item's description — `ReadSpecAsync` leads
  the description with a code-written `Repo:` line when the item has none, which is how every source tells the pipeline where an
  item is worked. A project with no `repository_url` leaves the item to `Factory:DefaultRepo`; a `repository_url` that names no
  github.com repository, and a project that is archived or gone, is refused (no `Repo:` line to fall back on), never worked
  somewhere else. Those two, and a `kb-<n>` the ledger never gave out ("… Run `factory kanban key <item GUID>` first"), are a
  `KanbanItemException` from `ReadSpecAsync`, raised before the pipeline has an item to escalate: `factory run` prints the message
  (as it does a `KanbanException` of that read) and exits 1, no stack trace (`FactoryRunner.RunCommandAsync`). `factory kanban
  key` likewise prints and exits 1 on a board refusal, an HTTP failure or timeout, and a ledger failure
  (`FactoryRunner.IsKanbanKeyFailure`). The spec is the item, its epic and the epic's documents; links are built on `Kanban:WebUrl`.
  Comments (E3, E4; `KanbanWorkSource.CommentKey`): a comment's idempotency key is the SHA-256 of the item's board id, the item's
  version when the comment is written and the body — board data only, never a ledger key or a factory counter. Two limits,
  accepted and unlike Shortcut (whose comments carry no key): (1) the same words said again while the item's version has not
  moved (a comment, a link and a claim leave it; a move or an edit bumps it) are dropped by the board, which answers the earlier
  comment, and the factory counts them as posted — e.g. an identical escalation comment after a re-run that moved nothing; (2) a
  retry of one comment after the version moved in between posts it a second time. No other board datum fixes (1) without
  breaking the retry: the board's latest comment (a cursor of its comment list) moves with the factory's own comment, so a retry
  after a lost answer would post again, and leaving the factory's own comments out of the cursor makes a repeat map to the
  same key once more (sc-25992).
  Trust (E5, sc-25984): trust is a property of the SOURCE, declared once on its naming (`ItemNaming.Trusted`: Shortcut and Kanban
  true — the owner's boards — GitHub issues false, and false for any source that does not say, fail closed) and asked in one
  place, `WorkStory.UntrustedName` = the source is not trusted, or the item is planner-authored (`PlannerAuthored`: its
  description carries the planned-item marker, which the code writes on every source — planner text is data on every source,
  and a person who rewrites the item without the marker has taken it over). Everything that states an item reads that one
  answer: the spec in every worker prompt (`PromptFence.Spec`), the name in commit messages and pull request titles
  (`WorkStory.PublicName`) and bodies (`LedgerReport`), and what a worker's input counts as (`RunPipeline.SpecInput`). So a
  Kanban item's spec is unfenced instructions and its name goes on GitHub as written, like a Shortcut story's; only a trusted
  source plans (`IWorkSource.PlansItems`). No default can trust (sc-25992): `IWorkSource.Naming` has no default implementation
  and `WorkStory.Naming` is a required, non-nullable parameter, so a source or a story that does not name its source does not
  compile (`SourceTrustTests.A_source_and_a_story_must_name_their_source_…`), and no Shortcut default sits outside `Shortcut/`.
  Epics (sc-25985, E4): a Kanban epic is a GUID too, and gets a ledger key of its own the same way — table `kanban_epics`
  (migration `KanbanEpics`; `KanbanEpicKey`: key, the epic's GUID under a unique index, its project's GUID), allocated by
  `KanbanKeys.EpicKeyAsync` when `ReadSpecAsync` first reads an item of the epic, so before the spec, the item's ledger row
  (`WorkItem.EpicId` = that key) or any control names it. Item keys and epic keys are two number spaces (`kb-1` the item and
  `kb-1` the epic may both exist); nothing of an epic key is written onto the board. `WorkEpic.Id` is that key (for Shortcut the
  epic's own number), never null. The same migration adds `kanban_items.ProjectId` (nullable: the item's project GUID, recorded
  with the key and again whenever the item is listed with none or another), which with `kanban_epics` is all the dashboard's links
  need (below); an epic's project is recorded again likewise when its row has another. An epic no item of which the factory has
  read yet gets its key ahead from `factory kanban key --epic <epic GUID>` (`FactoryRunner.KanbanEpicKeyAsync` →
  `KanbanWorkSource.EpicKeyOfAsync`: like the item form it reads the epic from the board first — `get_epic`, read only; one the
  board does not know or will not show the token gets no key row, exit 1 with the message, `FactoryRunner.IsKanbanKeyFailure` —
  records GUID → key and project, prints `kb-<n>`, and is idempotent, the unique index deciding a race), so `factory pause|stop
  --epic kb-<n>` works before its first item is claimed: an item of it read later gets the same key and is held by the row. The
  command takes exactly one of an item GUID and `--epic <GUID>`. The pipeline page shows the key beside the item once one is read.
- GitHub issues (sc-25385, `Issues/`): `IssueIntake` polls each watched repo's open issues updated since its ledger cursor
  (`github_issue_cursors`; the first poll of a repo starts from now, no backfill). Each issue gets a key (`github_issues`) and a
  work item `gh-<key>` that waits Paused/parked (`awaiting triage`) until released. Each issue version (sha of title+body) is
  triaged once by a read-only, unpinned worker (`WorkerTriageRunner` over `WorkerTools.ReadOnly`: its only allow rule is
  `Read(//<abs triage worktree>/**)` (`WorkerTools.ReadRule`; Claude Code bounds Glob/Grep by Read rules), `--permission-mode
  dontAsk` (every call that would prompt — any read outside the working directory — is auto-denied), `--settings` with
  `permissions.blockReadsOutsideWorkingDirectories`, `--setting-sources ""` (no repo or worker-user settings file can widen
  it), every known write/exec/sub-agent/worktree/web tool in `WorkerTools.WriteOrExecTools` denied by name (belt and braces: not
  exhaustive as the CLI adds tools — the guarantee is `dontAsk` with no allow rule but the `ReadRule` and the allowlisted MCP
  tools below, so any unnamed tool is auto-denied too); the runner refuses any worker whose tools
  are not `IsReadOnly` (which accepts, beyond the `ReadRule`, only allow rules in `McpServers.AllToolRules`), so a tainted session cannot change a file an untainted session later
  pushes, E4; the prompt has it reason from the code, building and running nothing; with CodeGraph configured it may also call,
  through the loopback MCP proxy below, only the triage profile's tools (`McpProfile.Triage`: `search_graph`, `get_code_snippet`,
  `read_node_source` — the CodeGraph tools whose answers stay inside the issue repo's pinned project; no Kanban and none of
  `analyze_impact`/`trace_call_path`/`find_consumers`/`find_publishers`, whose answers cross into other private indexed projects:
  the issue text is untrusted, possibly an outsider's, and the triage's summary and fix are posted on the issue, possibly
  public), and its prompt names them, `TriagePrompt.McpNote`; its `--allowedTools` names exactly those three,
  `SandboxTriageRunner.TriageTools`) in a throwaway `factory/triage-gh-<key>`
  worktree under its own root (`<WorkRoot>/triage-worktrees`, `SandboxTriageRunner.TriageWorktrees` — with several worker slots
  the root of the slot the triage runs in, `triage-worktrees-n` for slot n, `TriageWorktreesOf`, sc-25990: "Slot isolation" —: never the items'
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
  directory) it is best-effort and unverified by the factory; a gap there would reach what `_factory` can read.
  Loopback MCP proxy (sc-25707, `Mcp/`, E5/E6): sessions that plan or triage reach CodeGraph and Kanban only through
  `McpProxy`, hosted by the process that runs the session (`SandboxTriageRunner` starts one per triage when CodeGraph is
  configured, with the CodeGraph upstream only, in `factory work` or whatever process triages, and stops it after; a failed
  sweep of leftover config files or proxy start is factory-wide, `FactoryUnavailableException`) on `127.0.0.1` only (a free port; never the dashboard's bind address; no
  configuration source is read, so no `Kestrel:Endpoints`/URLs can add a listener, and start-up fails unless it listens on
  exactly one `127.0.0.1` address). It holds the upstream tokens (`McpUpstreams`, the one registry it shares with the
  reviewers' tool loop: `CodeGraphMcpClient.Upstream` and the Kanban `McpUpstream`, both over the generic `McpHttpClient`);
  each grant is a per-session tool set, `McpProxy.Grant(repo, McpProfile)`: `McpProfile.Triage` (above) or
  `McpProfile.Planner` (every allowlisted CodeGraph tool and Kanban read tool; a plan item's planner session, sc-25870; the only
  profile granted several repositories, `McpProxy.Grant(repos, McpProfile)`, below), each a subset of the
  upstreams' allowlists (checked when the profile is made). The session gets `--mcp-config <file>` naming
  `http://127.0.0.1:<port>/mcp/<server>` for each server its profile grants a tool of, with a
  per-session bearer credential (32 random bytes, kept hashed), plus `--allowedTools` rules
  `mcp__<server>__<tool>` for exactly its profile's tools (`WorkerTools.WithMcp`; refuses any rule off the allowlists, and any
  base tools that are not read-only and confined, so an implementer can never be given one), and keeps
  `--strict-mcp-config`. Requests need `Host` `127.0.0.1:<port>`/`localhost:<port>` and no `Origin` (else 403) and a live
  credential (else 401; not the dashboard's cookie login); a server not configured, or one the grant has no tool of, is 404.
  `tools/list` shows only the grant's tools; `tools/call` refuses any other with JSON-RPC error -32602, nothing forwarded, and
  so every call past `McpProxy.MaxCallsPerGrant` (100) per grant; every relayed answer (CodeGraph's and Kanban's) is its text
  cut at `McpProxy.MaxAnswerChars` (60,000 chars and UTF-8 bytes) with a `[cut: …]` marker, a CodeGraph answer first labelled
  with the commit it describes as the reviewers' are ("CodeGraph's index of <project>'s default branch as of <sha>", or "…, commit
  unknown"; a review adds ", not the PR head <head>", a proxy session has no PR head; `ReviewTools.DefaultBranchLabel`, E3), which
  the CodeGraph tools' descriptions and the triage prompt say. One allowlist per
  upstream: CodeGraph's is `McpServers.CodeGraphTools` — the reviewers' `ReviewTools.CodeGraphTools` (7 model-free tools: all the
  reviewers' tool loop offers and all `CodeGraphMcpClient.CallAsync` sends) plus the planner's cross-repository
  `PlannerCodeGraph.Tools` (6, below; the proxy sends them with `CodeGraphMcpClient.CallProxiedAsync`) —, Kanban's `McpServers.KanbanTools` (the hosted
  server's exact snake_case names, read from its `tools/list` on 2026-10-12, sc-25710: list_projects, get_board, list_epics,
  get_epic, list_epic_documents, get_epic_document, list_work_items, list_epic_work_items, get_issues, get_work_item,
  search_work_items; names match exactly, so a PascalCase spelling is never offered; no create_*/update_*/delete_*/move_*/
  add_*_comment tool is ever offered or forwarded). CodeGraph is repo-scoped like the reviewers'
  tools: the grant's repository's project (exact repo URL match, resolved once per grant), a call naming another `project`
  refused (-32602), the arguments rebuilt by `ReviewTools.CodeGraphArguments` (unknown ones dropped) with the project set,
  `read_node_source` shown only for a node of that project, schemas listed without a project argument. Kanban has no repository
  mapping, so it is NOT repo-scoped: its read calls go through with the session's arguments. The credential never goes in argv
  (every local user can read `ps`): `McpProxySessions` writes the config to `<WorkRoot>/mcp-sessions/<guid>.json` (slot n's
  directory with several worker slots: `mcp-sessions-n`, made that slot's user's alone to read and checked so, sc-25990: "Slot
  isolation"; directory
  `0700`, file created `0600`; the worker user reads it through the work root's inherited read ACL, which the owner checks
  with `/bin/ls -led` once the file is written — a `search` entry for `Worker:RunAs` on the directory and a `read` entry on the
  file (`WorkerSandbox.Grants` over `WorkerSandbox.ListAclAsync`, the one ACL reader and parser, read up to
  `WorkerSandbox.AclReads` times as every sandbox ACL check is, sc-25992; no deny), else the session is not started, factory-wide, "re-run `sudo
  scripts/setup-worker-user.sh`"; not checked with `Worker:RunAs=none`; outside the triage
  worktree, so the confined session cannot Read it into its transcript) after checkout and before launch, and when the session
  ends deletes it and revokes the credential (`McpProxyGrant.Dispose`: refused from then on); leftover files are swept before
  each triage (a live run's are kept, sc-25987: "Clean-up under overlapping runs"). Residual, accepted: while the session runs, a process that can read that file — the owner, root, or another
  process of the same sandbox user (none runs then: the session holds that user's lock, `WorkerLock`; with several worker slots that
  is the session's own slot's user and lock, and no other slot's user can read the file, sc-25990) — could call the session's granted tools through
  the proxy. Upstream tokens are only in the owner process (never in the worker's env, argv, worktree, prompt or transcript;
  `McpProxyTests`). Every MCP tool use still taints the session (`mcp:<tool>`; triage is already tainted by the issue text and
  never pushes). Implementers and fixers get no MCP: their tools allow none, and `ClaudeWorker.BuildArguments`/`RunAsync` refuse
  an MCP config for them (`--strict-mcp-config` and no `--mcp-config`). A plan item's planner session (below) uses the same `McpProxy`/`McpProxySessions` under `McpProfile.Planner`.
  Planner cross-repository tools (sc-25874, `Mcp/PlannerCodeGraph.cs`): the planner is the one agent that sees every repository of a
  plan. Its profile's CodeGraph tools are the reviewers' seven plus `search_projects`, `analyze_impact_file`, `trace_data_lineage`,
  `get_service_clusters`, `get_cluster_detail`, `file_hotspot`, each checked against CodeGraph's source (main at f95d231) to reach
  no LLM and no embedding model at call time and to return no stored model text (graph store, `cross_repo_edges`, `repo_clusters`
  — Louvain arithmetic, labels never written — and file metrics only; the reason per tool is the comment on the list; none was
  dropped). `get_architecture`, `get_service_summary`, `get_project_health`, `project_report`, `search_conventions`, `rag_search`,
  every memory tool and the Shortcut/RabbitMQ/MySQL hub tools stay off every list (stored or live model output, or external
  systems; `PlannerCrossRepoProxyTests` pins the reviewers', the triage's and the planner's lists and that none holds one). The
  reviewers' list (`ReviewTools.Names`) and `McpProfile.Triage` are unchanged, and no other profile holds a cross-repository tool.
  Their arguments are rebuilt by `PlannerCodeGraph.Arguments` (unknown ones dropped, strings cut at 300, depth 1–5, page 1–1000,
  page size 1–100, paths `RepoPath`-normalised and at most `MaxPathChars` (1,000; a longer one is a usage error), never a `sha`: the default branch) and the project set by the proxy (as `repo` for
  the two per-file tools, CodeGraph's own name for it). `search_projects`, `get_service_clusters` and `get_cluster_detail`
  (`PlannerCodeGraph.Global`) are not about one project: they are sent with no project, labelled "CodeGraph's <tool>, across every
  project it indexes (default branches)", and their answers name every indexed project, private ones outside the plan's set
  included. Residual, accepted (the owner's Phase 3 rule): the planner reads the owner's own plan story, not an outsider's text;
  triage, driven by untrusted issue text and posting on the issue, never gets a cross-project answer. Where the planner's output
  goes, said plainly (sc-25877): not only to the owner's board — the titles of the stories it writes and the plan's `interactions`
  note also reach GitHub, in pull request titles and bodies and commit messages of the plan's repositories (always inert or
  fenced, E3). So a planner can restate what a cross-project answer named — another private indexed project's name, a symbol of
  it — into a pull request body of the plan's repositories. That is the accepted residual: do not run a plan on a public
  repository whose readers must not learn the other indexed projects' names.
  Residual, accepted: "no stored model text" for `get_service_clusters`/`get_cluster_detail` depends on CodeGraph never writing
  `repo_clusters.cluster_label` (its handlers print `cluster.Label ?? "Cluster N"`); checked at CodeGraph f95d231, and to be
  re-checked — the two tools dropped if the label becomes model text — when CodeGraph gains a cluster labeller.
  Multi-repository grant: `McpProxy.Grant(repos, McpProfile.Planner)` pins CodeGraph to a set of repositories (any other profile
  with more than one is refused; duplicates, case-insensitively, collapse), each project resolved by exact repo URL match, once per
  session. With more than one repository every tool about one project lists a required `repo` argument (an enum of the set's
  `owner/name` values) and a call must name its repository — by `repo` (case-insensitive) or by a `project` that is exactly one of
  the set's CodeGraph projects; naming another repository or project, two that disagree, or none is refused with JSON-RPC -32602,
  nothing forwarded (the selector itself is never forwarded). `read_node_source` takes no `repo` and is shown only for a node of a
  project of the set. With one repository nothing changed (no `repo` argument is listed and a `repo` sent anyway is dropped like any
  unknown argument — except on `analyze_impact_file`/`file_hotspot`, where `repo` is CodeGraph's own project argument: there one
  naming anything but the grant's repository (`owner/name`, case-insensitive) or its pinned project (exact), or not a string, is
  refused -32602 with nothing forwarded, like another `project`; the descriptions and labels are the same): triage's grant behaves as before. The call cap, the answer cap and
  the commit label are per grant and per answer as before; the tool descriptions and the planner prompt (`PlanPrompt.McpNote`) say
  how to name a repository and which tools are not about one. The intake reads and writes the issue board only through the issue source's
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
- Repository manual controls (sc-26337): `pause|continue|stop --repo owner/name` uses the canonical case-insensitive
  `repo:owner/name` scope, persisted as untruncated text separately from automatic incidents. A repository Stop leaves a
  human Pause until explicit human Continue, cancels the repository's active items, and drafts only that repository's
  changeset PRs. It does NOT turn a multi-repository coordinator into an unrestricted plan Stop: the coordinator stays
  nonterminal so independent children in other repositories can continue; cancelled children keep the original plan
  incomplete, and their dependents are not released. Unstarted work and retained coordinator delivery in the stopped
  repository remain held by its manual Pause. Continue lifts that Pause only, never resurrects cancelled items or clears
  an incident. Scoped drafting takes the coordinator's run lock; an active run or possible crashed worker leaves Stop
  explicitly unfinished, the repository paused, and names the required retry rather than drafting underneath it. Item,
  epic and factory Stops retain their existing full-cascade semantics. Dispatch resolves a fresh spec and any recorded
  changeset pass before evaluating repository recovery, while human/global/plan controls and Stop take precedence.
- Hot-file recovery (epic sc-26335, E1–E12; `Recovery/`, `RunPipeline.Recovery.cs`): `Freeze:HotFileMerges` (8) distinct
  factory Merge rows touching one normalized file of one repository (case-insensitive key) and exact base within
  `Freeze:HotFileWindowHours` (24, inclusive) open a `hot_file_incidents` row for THAT repository only — never the global freeze
  row, never another repository; several hot files coalesce into one incident with their own trigger tuples, and other
  repositories recover independently. Lifecycle (`HotFileIncident.State`): Held → Assessing → Resolved (safe), or → Unsafe →
  Correcting → (delivery) Reassessing → … → Resolved; pending-only unsafe → redirect + Refreshing → Reassessing; inconclusive or
  unusable → Held and retried; any exhausted branch → Escalated (sticky). Every state change, and every new generation, is an
  append-only ledger row `recovery-incident-state` saved in the same transaction (`IncidentTransitions`, hooked into
  `LedgerDbContext.SaveChanges`; anchored on the first contributing merge's item; typed deferred, a resolution passed — never
  escalated, so no item's escalation metric counts an incident). Durability: the repository's advisory lock
  (`HotFileRecovery.LockKey`) serializes detection, dispatch and acceptance across processes; `Version` CAS tokens; an
  attempt is reserved (`recovery_assessment_attempts`, leased `StageTimeout` 15 min: a 5-minute capture plus the 10-minute
  Review conversation) and committed BEFORE dispatch, so a crash spends it (`Uncertain`) and nothing replays endlessly; usage
  waits and shutdown spend none. Bounds (E11): 3 assessment sessions per evidence generation, 2 correction PR attempts per
  generation (`RecoveryCorrections.CorrectionLimit`; attempt numbers stay monotone per incident, a generation counts from the
  last attempt before it, `BaselineAsync`), 3 evidence refreshes per incident (reset only by a human re-arm); a failed or inconclusive assessment
  retries after 30 s, then 120 s; at most one assessment per repository and two across processes (two advisory seats beside a
  process limiter); snapshots refuse more than 50 contributing merges, 100 pending items or 2 MiB as incomplete, never safe. A capture's body
  is stored once per content hash in `recovery_materials` (`RecoveryMaterial`, migration `RecoveryMaterials`) before any
  router call, and its `recovery-snapshot` ledger row is a small `RecoveryCapture` naming that hash (with what was missing),
  so captures at every authority boundary never add multi-MiB rows to an item's history; the full material stays in the
  assessment's own answer.
  Assessment (E4/E5): a fresh router session on `high` (`RouterHotFileAssessor`, the compiled-in `hot-file-assessment`
  prompt, read-only tools pinned to snapshot commits; the request carries an index of the evidence — each entry's id,
  category, provenance, commits, SHA-256 and size, `RouterHotFileAssessor.Index` — and the model reads each body through
  `read_evidence`, so the tool budget is not spent on inline material), its answer bound to incident, generation, snapshot, repo and evidence
  hash and parsed strictly (`RecoveryDecision`); only the exact current safe answer resolves, acknowledging exactly its
  (merge row, base, file) tuples beyond the window and restarts (a later merge of a reviewed file opens a new generation).
  Correction (E7/E8): an accepted unsafe merged finding materializes ONE `rc-<n>` work item per attempt in exactly the
  incident's repository and recorded base (`RecoveryWorkSource`, internal, no board credential; its spec is planner-like data),
  with a ≤ 20-path envelope checked against the current policy and floors. Its grant (`RecoveryCorrections.Grants`: Pending or
  Claimed, the repository, the item not Cancelled, its own incident Correcting at the same generation and evidence hash, no
  other unresolved incident in the repository, no stopped ancestor) excepts it from its OWN incident's hold only. The full
  revalidation — a fresh evidence capture, the current policy, the envelope and the diff (`AuthorizeCorrectionAsync`) — runs
  once at each authority boundary: dispatch (`RunAsync`), worker entry (`RunWorkerSessionAsync`), push (after the clone's lock,
  right before the remote write; the pre-staging and pre-token checks are structural, `PushGrant.PrecheckAsync`) and merge (after
  the write token, then re-read: S6). Every in-loop and wait check (each step, each poll of a slot, CI or overlay wait) is the
  structural `EffectiveAsync` composition only, which already contains the grant predicate — no capture (`RecoveryCorrectionPipelineTests.A_correction_captures_evidence_once_…`).
  The correction then runs the ordinary panel, CI, new-tests, taint, gate and merge queue unweakened; a gate merge is not a safe
  verdict: the delivered commit is reassessed afresh (E10), and affected pending work restarts fresh from the corrected base
  (`RecoveryRedirect`). A disposition is revalidated against fresh evidence (`HotFileRecovery.RedirectAsync`) only while the
  item has not yet acted on it and its incident is the repository's newest: once its exact `recovery-restarted` row exists it
  is returned as recorded with no capture, lock or write (another item's merge moving the base, or another pending item
  advancing, says nothing about a restart already made); one the item outgrew without restarting (Merge, Watch, Done,
  Cancelled, a plan coordinator) is void; one of an older incident is returned without capture when the newest incident is
  resolved and deferred while it is not. Only the newest incident's own unconsumed disposition may send that incident back to
  Refreshing on a mismatch, so an old generation is never reopened. Every unresolved incident of a repository holds it, and
  every one is advanced (`AdvanceAsync`, oldest generation first) and re-armed (`reassess` re-arms each escalated incident,
  each to the next generation after all of the repository's). Held work: an item a hold stops mid-run is recorded `recovery-held` when only the incident holds it
  (resumes by itself once it resolves; dashboard phase "held by repository recovery"), and so is a correction whose boundary
  authorization refused or could not verify it (a busy repository lock, a capture that cannot be read:
  `ControlRequestedException.CorrectionDeferred`); `user-paused` only when a human Pause holds it too (then both must lift).
  Such a deferral (no proof the grant is stale; a busy lock is waited for first, `HotFileRecovery.LockWait` 5 s on the injected
  clock, at most 100 tries) is counted on the correction (`recovery_corrections.Deferrals`, migration
  `RecoveryCorrectionDeferrals`), each its own refusal row (count and time), and no boundary is tried again before `RetryAt`
  (30 s, then 120 s): a poll inside the backoff captures and writes nothing; after `HotFileRecovery.DeferralLimit` (3) in a row
  the incident escalates naming the reason and `factory recovery reassess`. Only progress past the boundaries — the push, the
  authorized merge (`HotFileRecovery.ProgressedAsync`) — resets the count, never a verified check alone (dispatch verifying
  and worker entry deferring still escalate).
  Recovery's own ledger rows are never evidence: one rule, `RecoverySnapshot.IsBookkeeping` (every `recovery-` step, and the
  slot rows), so rows anchored on a pending item (a Planned coordinator) cannot revoke the grant they record. Human supremacy (E2): no recovery stage writes a control; a safe answer never synthesizes Running;
  a Stop is never undone; `continue --repo` lifts only that Pause, not an incident. Re-arm (E11): an Escalated incident holds
  its repository until a human runs `factory recovery reassess --repo owner/name` (owner CLI) or the dashboard incident panel's
  Reassess (login + antiforgery, `DashboardControls.ReassessPath`; a refusal answers 409 with its reason, nothing written): the SAME incident moves to a new generation over its
  recorded merges with fresh budgets (assessments, refreshes and corrections), revoking the old generation's waiting
  correction grants, with one audited `recovery-reassess` ledger row (who, when, incident, old and new generation), under the
  repository lock; it clears no Pause or Stop, leaves the freeze, usage and other incidents byte-identical, and a stopped
  correction still refuses a replacement. Nothing else may call it (`RecoveryReassessTests.Only_the_cli_and_the_dashboard_post_…`
  scans the source). Drivers (E12): `factory work` schedules each candidate repository (an unresolved incident or a merge
  inside the window, `CandidateReposAsync`: a fixed number of queries) off the poll task, not in a worker slot; `factory run`
  advances its item's repositories inline before taking the item's run lock. Upgrade: a legacy global `hot-file` freeze row is
  never cleared or acknowledged by recovery; status and the dashboard name it. Operators: `factory recovery status [--page N]`
  and the dashboard's repository recovery panel read the ledger only (`RecoveryStatusReader`: state, generation, `assessment
  n/3, correction n/2, refresh n/3`, hot files, evidence, the fresh session, corrections with repo-scoped PRs, held and
  redirected work, applicable controls, the named remedy); an idle dashboard tick polls only that panel. Tests: `HotFileRecoveryTests`,
  `Recovery*Tests` (assessment, snapshot, work source, correction pipeline and crash, redirect, refresh, reassessment, continuation,
  control, dashboard, status, Postgres, re-arm), `RecoveryAcceptanceTests` (AT1–AT7 integrated), live AT8
  `HotFileRecoveryAcceptanceTests` with `HotFileLiveTests` (docs/acceptance.md, "Autonomous hot-file recovery").
- Controls (`Controls/`): Pause/Continue/Stop rows in the ledger's `controls` table (scope `factory` | `epic:<id>` |
  `epic:kb-<key>` | `item:sc-<id>` | `item:gh-<key>` | `item:kb-<key>`; `WorkItem.EpicId` maps items to epics, within the item's
  own source — epic scopes, sc-25985, at the end of this bullet; a plan item's `item:` scope also holds its stories, through
  `WorkItem.PlanItemId` — plan failures and controls, sc-25876, below), written by `factory pause|continue|stop` and the dashboard
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
  Epic scopes carry their source (sc-25985; `EpicRef` = an `ItemNaming` + the id the ledger holds, `ItemNaming.Epics`:
  `EpicNaming.Number` | `Key` | `None`): `epic:12` is Shortcut epic 12 — what it always meant, so every row written before
  Kanban epics had scopes still applies to Shortcut items, and only to them (E9) — and `epic:kb-12` the Kanban epic with ledger
  key 12 (`kanban_epics`, under KanbanBoard items above); a GitHub issue is in no epic. One rule turns a ledger row into its epic,
  `ItemNaming.EpicOf(externalId, epicId)`, used by `LedgerControls.EffectiveAsync` (still one query: the item's source comes from
  its external id, so the epic scope is one more string in the same `IN` list), `RunPipeline.UserPausedAsync` and the dashboard;
  `IControls.EffectiveAsync`'s signature did not change. `ControlScope.Epic(EpicRef)` writes a scope and `ControlScope.EpicOf`
  reads only what that writes (`epic:KB-3`, `epic:012` are not scopes: no item's control would read such a row).
  `factory pause|continue|stop --epic` takes either form (`ItemNaming.ParseEpic`: a bare positive number is a Shortcut epic, `kb-N`
  in any case a Kanban epic; `sc-N`, `gh-N` and anything else are refused by the parser). `ControlActions` reaches only the
  epic's own source: Stop lists the active items with that source and `EpicId`, and both Pause and Stop look up that source's
  active items with no `EpicId` on their own board (`IWorkSource.ReadSpecAsync`, E1 — for Kanban reads only, which allocate the
  epic's key if needed). An epic its source says cannot exist is refused before anything is written
  (`IWorkSource.KnowsEpicAsync`: true by default — any number may be a Shortcut epic, pausable before the factory has seen it; a
  Kanban key the ledger never gave out is refused, "The factory has seen no KanbanBoard epic kb-7 …", since a row for it would
  land on whichever epic is given that key later; the refusal names the remedy, `factory kanban key --epic <epic GUID>`). The
  check needs no credential: where the epic's source is not configured (no Kanban write token, or a dashboard host with no
  registry) its kind answers from the ledger alone (`WorkSourceKind.KnowsEpic`, as `Links` does: `KanbanKeys.KnowsEpicAsync`), so
  an unknown key is refused there too, while a known epic's control is written whether or not its board can be reached (its
  items with no recorded epic are then reported as unknown, as before).
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
  With several runs in flight (sc-25991): the run whose worker hit the limit pauses the factory and itself; the loop reads the
  pause before it starts another ready item of that poll and starts none (shown as a deferral with its resume time,
  `IntakeStatus.Deferrals`); every other run in flight pauses itself `usage-paused` at its next check, session and slot kept;
  no run is counted as failed; and all of them resume by themselves once it lifts.
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
  (a state: per repo the factory works on now — the configured repos, `FactoryOptions.ConfiguredRepos` = `Factory:DefaultRepo`,
  `Factory:Repos` and `GitHub:Watch:Repos`, and the repos of non-terminal items — the base branch's head after its latest factory merge over all history
  is `Ci.Evaluate` Failed; pending is not red; a base the compare answers 404 for under a working installation
  (`GitHubNotFoundException`: the branch or merge commit is gone) has no tip, so nothing to be red; a gate App that is not
  installed on the repo or cannot see it (`GitHubAppNotInstalledException`: the installation lookup's or token mint's 404)
  fails the check instead (`freeze-check-failed`, held as `check-failed`) — a `FreezeStatus.Notes` line, logged `[freeze]`; a head a Continue
  acknowledged — the `red-tip <repo>@<base> <sha>` lines of the cleared freeze's `Detail`, carried into later freezes — is skipped
  until the head moves). The run then returns the typed outcome `deferred` (`RunOutcome.Deferred`, nothing about the item changes;
  the error says what lifts it, `FreezeTrigger.Remedy`); the intake loop runs no ready item of any lane after a deferral and stops a
  lane's in-flight items at its first deferral (their stops, listed first, still run in every lane), lists no ready items while the
  row is set, and shows the deferral (`IntakeStatus.DeferredRun`, not cleared by a later stop) and the freeze banner on the
  pipeline page. With several runs in flight (sc-25991) a freeze written mid-poll is read before each ready item would start, so
  none is dispatched into it (shown as a deferral, `new work held: the factory is frozen (<trigger>)`, with what lifts it); the
  runs in flight each pause `freeze-paused` at their next check and all resume after the Continue; every run the evaluator
  deferred in the poll is listed (`IntakeStatus.Deferrals`), not only the last. Each intake poll runs the evaluator too (`IntakeLoop` `freeze`, `FactoryRunner.CreateFreeze`) before any lane
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
  comment, and a plan item waiting on its stories (`Planned`) one comment and one `plan-held` row per reason, staying `Planned`
  (sc-25871). Factory-wide failures (`FactoryUnavailableException` from `FactoryRunner`'s shared set-up: the migration at a run's
  start; the sandbox probe and the worktree sweep at its first worker phase; a `DbException`; `MissingCredentialException`; a
  failed poll) end the poll and escalate nothing — with several runs in flight (`Intake:MaxConcurrentRuns`) no further item is
  started, the runs already started run to their end, and the poll shows the failure once. Another run's worker phase holding the
  sandbox user is no failure: a run waits for it. Failures are counted per item whatever runs beside it. Both show on the
  pipeline page (`IntakeStatus`, in memory in the `factory work` process).
  Runs in flight (Phase 4b, sc-25991; `IntakeLoop.PollOnceAsync`, `IntakeStatus`): a poll's runs overlap, so what stops new work
  can happen while others run. The rules, one per cause — each stops what is NOT yet started, and never ends a run that is: the
  runs in flight reach their next check (before every step, every second while a worker runs or a slot is waited for), where the
  existing controls pause them (`freeze-paused`, `usage-paused`, `user-paused`) or they finish by themselves.
  (1) A factory-wide control found in effect — the usage pause (e.g. set by a sibling run whose worker hit the limit), a Pause
  of the factory, the freeze's row: read at the poll's start (nothing ready is listed, no triage) and again before EACH ready
  item is dispatched (`IntakeLoop.HoldsAsync`; the poll listed its work a while ago). Held, no ready item of the poll is
  started; the in-flight items the poll listed are still run, each run pausing itself at its first check or finishing its Stop
  (as the serial loop always did). Controls that cannot be read then are the factory's failure (E2: never dispatch blind).
  (2) A run the freeze evaluator defers (`RunOutcome.Deferred`): no ready item of any lane and no further in-flight item of its
  lane, as before. (3) A factory-wide failure of one run (a stale helper in its slot, say): nothing more is started this poll;
  it is not that item's failure and never a sibling's — a run beside it that fails for its own reason is counted against
  itself alone, one that succeeds clears its own streak. What the status shows: `IntakeStatus.FactoryError` once per poll
  however many runs and listings hit it (`FactoryFailedAlso`: the count stays one per poll, a different reason is appended
  `; also: …`); and `IntakeStatus.Deferrals`, every deferral with what lifts it — each deferred run of the poll (not only the
  last; `DeferredRun` is still the latest), the poll's own evaluation (`intake`), and each control holding new work (`Hold`,
  ids `usage`/`factory`/`freeze`: "new work held: the factory is paused for usage (…); it lifts by itself at <utc>, or with
  `factory continue --usage`"), kept until the loop's next read finds it lifted (a poll's start, at most a poll interval after
  the Continue; the page's banners are the ledger's and current). One deferred run alone renders as the line it always was.
  Tests: `IntakeDispatchTests` (signalled fake runs), `RunsInFlightTests` (the real loop over real pipelines, two at once: the
  freeze, the usage pause and a stale helper in one slot, each with both runs' ledger rows and the status).
  Runs across polls (sc-25992, `IntakeLoop.StartPollAsync`): a poll does NOT wait for its runs — before, a poll ended only when
  every run it started had, so one item waiting 30 minutes on CI held back every other lane's new items and every claim renewal
  (once per poll) for as long. The loop now polls on its interval while runs are in flight. Each poll renews every lane's claims
  first; then lists every lane's in-flight items and starts, at once and OUTSIDE the run capacity, the items being stopped and
  the plan items waiting on their stories (`InFlightItem`, from `IItemRunner.InFlightItemsAsync`/`RunPipeline.InFlightItemsAsync`;
  a waiting plan only while nothing holds new work — else it is queued like any in-flight item, so the freeze's, the usage
  pause's and a factory Pause's rules for it are unchanged; a stop always, as before): they do not count against
  `Intake:MaxConcurrentRuns`, are still one run per item (the loop's in-flight set and the item's run lock), and whatever they
  do as the sandbox user goes through the slot lock (`WorkerLock`), so `Worker:Slots` is never exceeded — a pending Stop (a
  plan's pending stories, say) and a `Planned` evaluation no longer wait for a run to end. Residual, accepted: which kind an
  item is, is the poll's listing; a state another process changes between the listing and the start (a `factory run` of the
  same plan item) is the next run's business. Then, only while there is free capacity (`Intake:MaxConcurrentRuns` minus the
  loop's counted runs in flight, `IntakeLoop.RunsInFlight`), each lane is prepared and its ready items listed, and the rest are
  started into that capacity in the poll's order. With none free nothing ready is listed or prepared, the earlier poll's
  dispatch keeps its place (it starts its next item when a run ends), and the poll is owed: the end of a run that frees
  capacity starts the next poll at once instead of at the next tick (the tick stays armed: no poll skipped or doubled; the loop
  is one task, so polls never race), as the serial loop polled at once on the tick that passed during its runs. An item the
  loop already has a run of is never started again; a poll that lists supersedes the previous poll's dispatch, which starts
  nothing more (its runs go on). The rules above (holds before each ready item, a deferral, a factory-wide failure) apply to
  each poll's own dispatch. A run's end wakes the loop to re-read the usage pause, so a run that paused the factory for usage
  still wakes it at the reset. Preparation off the loop's task: a lane's preparation (`IntakeLane.Prepare`, the GitHub issues
  triage) is started by a poll that lists and runs in the background, at most one per lane at a time (so never two triages of
  one issue), while later polls go on renewing claims and the run-end wake-up works — a triage waiting up to
  `Worker:TimeoutMinutes` for a worker slot holds up nothing. A triage shares the worker slots with the items' worker phases
  through `WorkerLock` (`SandboxTriageRunner` holds a slot from its readiness check to its worktree's removal), so it never runs
  as a sandbox user another phase is using. Its end wakes the loop for a poll at once that starts no preparation and lists
  what it released; a preparation that failed is that poll's factory-wide failure (`Poll failed: …`, the lane not listed by it,
  as an inline failure skipped it; per-issue failures are counted by the triage itself, unchanged). Stopping `factory work`
  waits for the runs and the preparations in flight, each ending on the cancelled token as before. Status across polls: a
  run's end clears the runs' deferrals only when its poll is the newest and that poll's own freeze evaluation did not defer it
  (so an older poll's run never clears a newer poll's `intake` deferral); a poll says the factory is fine only once it has
  listed, its dispatch has ended, it is the newest that listed and no poll after it hit a factory-wide failure — a poll with no
  capacity free lists nothing ready and says nothing of the factory, so a listing failure stays shown until the board is listed
  again. Defaults (E9): with `Intake:MaxConcurrentRuns` 1 the order of runs is unchanged; what differs from the serial loop: a
  ready item is no longer handed to a run that would claim nothing when the factory was paused since the poll began, and a
  paused or frozen factory's page says once more, in the deferral list, what holds new work; a poll runs every interval also
  during a run — claims renewed, the router's usage read (`usage.CheckAsync`), the freeze evaluator run, its main-red GitHub
  reads cached for `FactoryFreeze.MainRedCacheTtl` (30 s) as before, so a long run costs those reads every `Intake:PollSeconds`
  where the serial loop made none; a stop and a waiting plan's evaluation run during another item's run; and a triage runs
  beside the item runs. Tests: `IntakeDispatchTests` (`The_work_loop_polls_again_…`, `A_poll_renews_the_claim_of_an_item_in_flight_…`,
  `A_poll_with_no_free_capacity_…`, `With_one_run_at_a_time_…`, `Stopping_the_work_loop_waits_…`, `When_a_run_ends_after_a_poll_found_no_capacity_…`,
  `Items_being_stopped_and_waiting_plans_…`, `While_something_holds_new_work_…`, the two deferral and two factory-error tests across
  polls, and the three preparation tests). `PollOnceAsync` (tests, and the old contract) is one poll waited for to its end, its
  preparation inline before the listing.
- Every state change is a committed ledger row before the next step (`WorkLedger.RecordAsync`), checked
  against the transition table in `Ledger/Lifecycle.cs` first; an illegal transition throws and writes nothing.
  New phases add handlers in `RunPipeline.Handlers`, not table rows (the exceptions, all Phase 4a's: `Plan → Planned`, a plan item
  waiting on its stories, sc-25870, `Planned → Done`, its plan finished, sc-25871, `Planned → Review`, a plan item taking its
  changeset's pull request through the gate, sc-25872, and `Merge → Planned`, a plan of several repositories waiting again after
  each changeset but the last, sc-25873). Sub-steps inside a state are checkpoint
  rows (`LedgerEntry.Step`, `WorkLedger.CheckpointAsync`); `factory run` on an existing item resumes from
  its last row and skips recorded steps (the Claude session id is checkpointed as soon as it streams, so an
  interrupted Implement continues with `claude --resume`). Ctrl-C → Paused; a failure (including an
  unrequested `OperationCanceledException`, e.g. an HttpClient timeout) → Escalated + a story comment; a failed
  comment is retried on the next run before the item is re-queued.
- Typed step outcomes (E7, sc-25389): every ledger row has `Outcome` (`passed`, `failed`, `gate_rejected`, `deferred`,
  `escalated`; NOT NULL + a check constraint), decided in one place — `StepOutcomes.Of(from, state, step, detail)`, called by
  `WorkLedger` for every row it writes (`LedgerEntry.Outcome` is `required`). The rules map earlier stories' typed results (verdict,
  fix-progress, gate decision, new-tests outcome, stuck rounds, pauses, issue routes; of Phase 4a's rows `plan-held`, `plan-stuck`, `plan-delivery` and `merged-into-changeset` are `deferred`; Phase 4b's `slot-wait`, `slot-held` and `slot-released`, sc-25991, are `passed` — a wait for a worker slot is no failure); migration `StepOutcomes` backfilled older rows
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
  data yet), escalation rate, fix rounds per merged PR, reviewer precision (blocking findings a second opinion confirmed / those it
  answered) and intake-to-merge time; N/A over zero matching items; sandbox/demo items are excluded (`Metrics:*`).
- Plan items (Phase 4a, sc-25870; `Plans/`, `RunPipeline.Plan.cs`): an item labelled `plan` (`PlanLabel`, any case) on a source that
  plans (`IWorkSource.PlansItems`: Shortcut and, since sc-25984, KanbanBoard — "Plans on Kanban" under the Kanban bullet; only a
  trusted source, `ItemNaming.Trusted`; false by default, so a GitHub issue's label — which anyone may apply — plans nothing)
  goes Intake → Plan instead of Implement, and is never implemented itself. Plan runs one planner session (E2): a fresh one every
  time (never resumed: its checkout is throwaway), on the `high` class (`WorkerModelClass.Planner`), confined like the triage worker
  — `WorkerTools.ReadOnly` (`ReadRule` of its own worktree, `dontAsk`, `--setting-sources ""`; `WorkerPlanRunner` refuses any other
  tools before checkout), in a throwaway `factory/plan-sc-<id>` worktree under its own root (`<WorkRoot>/plan-worktrees`,
  `SandboxPlanRunner.PlanWorktrees` — with several worker slots the root of the plan item's slot, `plan-worktrees-n` for slot n,
  `PlanWorktreesOf`, sc-25990: "Slot isolation"; contents-read token only, not shared for writing, out-of-tree symlinks removed first, swept
  with the leftover MCP session files before each planner session when sandboxed, i.e. under the sandbox user's lock) — and reaching
  CodeGraph and Kanban only through the loopback MCP proxy under `McpProfile.Planner` (started per session by `SandboxPlanRunner`
  when an upstream is configured; its `--allowedTools` name exactly that profile's tools, `PlannerTools`). With `Worker:RunAs=none`
  (development only) the planner still runs — unlike triage, which refuses: its input is the owner's own story, the same trust as
  an unsandboxed implementer — but as the owner, so its read-only confinement is then only Claude Code's own (`dontAsk`, the
  `ReadRule`, the denied tools): no `_factory` user and no ACL stand between it and the owner's files, and nothing is swept.
  Repository set (sc-25874): a plan item may name several repositories, one `Repo: owner/name` line each
  (`RepoResolver.ResolveAll`: in order, repeats dropped, lines inside a ``` or ~~~ fence not read — a planner-created story
  carries planner text in a tilde fence, which must not widen a later planner's set; `RunPipeline.PlanRepositories`). The first stays the item's primary
  repository — the ledger's `Repo` (except while the plan item takes another repository's changeset through the gate, restored
  when that pass ends: the `WorkItem.Repo` rule under sc-25873 below), the repository of a created story that names none; non-plan items
  still read only the first line. Every repository a plan item names — one line, several, or none (the default repository,
  configured by definition) — must be one of the factory's configured repositories (`FactoryOptions.ConfiguredRepos`:
  `Factory:DefaultRepo`, `Factory:Repos` — the setting for a repository the factory plans and works in without triaging its
  issues — and `GitHub:Watch:Repos`, passed to the pipeline as `configuredRepos`; matched case-insensitively,
  `RepoRef.Is`), else the item escalates before any session, seat or grant, naming the unconfigured ones ("The plan names 1
  repository (o/r), but o/r is not configured for this factory … Add it to Factory:Repos (GitHub:Watch:Repos would also turn on
  the triage of its issues), or remove its Repo: line."), with nothing created (epic E1: "only repositories the factory
  is configured for"; sc-25877 — before it only a plan naming several was checked; a non-plan item is unchanged and may name any
  repository). With one line or none the planner is seated exactly as above; with several the seat is one directory — where the primary repository's plan worktree would be, so the same sweep
  finds it — holding a `factory/plan-sc-<id>` worktree of each repository's default branch at `<owner>/<name>` under it
  (`ISeatWorkspace`, `GitWorkspace.PrepareSeatAsync`/`RemoveSeatAsync`; each clone fetched with a contents-read token minted for
  that repository, nothing shared for writing, nothing ever pushed), the worker's single `ReadRule` being that directory; the
  out-of-tree symlink removal runs per checkout (a link into another repository's checkout counts as outside and is removed); the
  proxy grant is over the whole set (above); and the prompt lists which directory is which repository (`PlanPrompt.Build`,
  `PlannerSeat.Checkouts`) and says every story is worked in the primary one. A
  seat the factory cannot set up (the sweep, the proxy start, the session file's ACL check: `FactoryUnavailableException`) is
  factory-wide, as for triage: the exception leaves `RunPipeline.RunAsync` (the only exception its catch-all lets through), the
  item stays in Plan with nothing recorded against it — no Escalated row, no comment — and the intake loop ends the poll
  (`IntakeLoop.IsFactoryWide`). A planner a crashed run left behind (`worker-started` with no `worker-done` since the item last
  entered Plan; `CurrentWorkerAttempt` starts at Plan too) is stopped before the next seat is set up (`StopOrphanAsync`,
  `orphan-killed`), like Implement's. The pipeline runs it like
  any worker session of the item (`RunSessionAsync`: pid, `session` and `worker-model-class` checkpoints, every line stored,
  Pause/Stop and the freeze watched, a usage-limited or `model_class_unavailable` session pausing the factory, never escalating)
  but without the repo-settings guard and the stuck detector (it loads no settings file and has no round), and taints it from its
  start (`Taint.Planner`), so nothing it did can be pushed. Models propose, code decides (E1): the answer's last fenced `json`
  block (`{"stories": [{key, title, description, acceptance[], type, blocked_by[]}]}`) is read by `PlanParser`, which refuses — never
  cuts — anything off the rules: 1–`MaxStories` (20) stories, unique `key`s (a-z, 0-9, `-`, `_`; ≤ 32) and unique titles (≤ 120),
  description ≤ 2,000, 1–5 one-line acceptance entries ≤ 300, type
  feature/chore/bug, `blocked_by` naming other stories of the plan, never itself, no cycle; text is cleaned of control and format
  characters, and the stories are ordered blockers-first; JSON that cannot be read — also a property named twice or a lone
  surrogate escape, which System.Text.Json reports only when the node is used — is refused the same way. Invalid → a
  `plan-rejected` checkpoint (why; outcome `gate_rejected`), the item escalates and no story is created. Valid → the `plan`
  checkpoint (the plan as JSON), then `plan-creating` right before the board is asked for anything, then
  `IWorkSource.CreateChildrenAsync` (the parent's team and epic, Backlog, `blocks` links), then `plan-stories` (the ids in plan
  order, e.g. `sc-12 sc-13`), then Plan → `Planned`. `CreateChildrenAsync` is idempotent, and finds a child again by its
  idempotency key (sc-25877, `ChildItem.Key(parentRef)`, the parent as its board names it: `dark-factory:child:<parent>:<SHA-256 of the name and the description as
  sent>`, written by code, the same on every retry of the same plan; Shortcut stores it as the story's `external_id`, sent on
  `POST stories`, and the listings carry it, so a child is matched from the listing and then read in full for its relations) —
  so a retry does not depend on the board returning a multi-line Markdown description unchanged. Two stories of one plan never
  share a key: `PlanParser` refuses a plan whose stories share a title, and the title is the hashed name (the rule that makes
  the key unique within a plan; `PlanParserTests.Unique_titles_are_what_keeps_…`). Fallback, only for a story with
  no external id at all (a child created before the key existed): name and description together (the description starts with the
  code-written marker below; the listings carry no description, so each such namesake is read in full; line endings and outer
  whitespace aside). Either way a story someone else wrote with a plan story's title — or one carrying another key (the same plan
  text under another parent) — is never reused, recorded or linked: the plan's own story is created beside it. Verified
  against the real API (2026-10-13, sc-25877): `write-lifecycle.json` is recorded with the key (Shortcut takes an `external_id`
  of the key's length — 19 + the parent's name + 1 + 64 characters: 92 for `sc-25858` — and returns it unchanged), and
  `release.json` records the retry finding both children by it in the team's listing, creating nothing (the epic's listing
  carries it too: seen on the live P4-AT1's stories; docs/acceptance.md). A source that could not find the children
  again refuses before any write (`ChildrenRefusedException`: a Shortcut parent with neither epic nor team, possible only with
  `--ignore-scope`), and the item escalates with no story created. Which plan a later run uses (`RunPipeline.ReusesRecordedPlan`;
  the newest `plan` row is the one considered): a recorded plan is reused — no planner session — when a `plan-creating` or
  `plan-stories` row follows it (a story of it may be on the board: no second plan's stories land beside the first's, E4, also
  after an escalation because the board was down), or when no Escalated row follows it (a crash, a pause, an interrupted run).
  A plan the item escalated on before `plan-creating` is dropped (the owner may have edited the story before re-queuing it, and
  nothing of the plan exists): the re-run plans afresh with a new session and records a new `plan`. A recorded plan that no
  longer passes `PlanParser` escalates saying so ("no longer validates"): before `plan-creating` the next run therefore plans
  afresh; after it every run says the same, and its stories are finished or archived by hand. With `plan-stories` recorded only
  the transition is left and the plan is not read again. `Planned` (a new state; table rows `Plan → Planned`, `Planned → Done`, `Planned → Review` and `Merge → Planned`) has no entry in `Handlers`: the
  item waits on its stories and is evaluated without a worker (dependency-aware release, below); waiting is not a failure
  (outcome `passed`) and gets no held notice. Planner text is untrusted (E3): each created story's description starts with the
  marker `<!-- dark-factory:planned-from sc-<parent> -->` (`PlannedStory`; the parent as its BOARD names it, `WorkStory.BoardRef`: a
  Kanban plan item's GUID, never its `kb-<n>`), then a `Repo:` line naming the plan item's repository
  (the first such line decides, `RepoResolver`: the planner's text cannot send a story to another repo; one repository per plan),
  then the planner's text in a tilde fence (no mention, link or image renders). A story whose description carries the marker is
  `WorkStory.PlannerAuthored`, so `UntrustedName`: its spec is fenced as data in every worker prompt (`PromptFence.Spec`) and its
  name is inert in commit messages and PR titles and a code span in PR bodies, like an approved triage. The marker is written by
  code, never by the model; a person who removes it has taken the story over. (Changeset branches, multi-repository plans,
  plan-scope controls and "held by" reporting are the bullets below.)
  Owner decisions pending confirmation (coordinator, 2026-10-13; each is implemented as described where it is documented, and each
  is the owner's to confirm or reverse):
  1. Hot-file excludes merges into a changeset branch (sc-25877, D): a plan's story merges never count, the changeset pull
     request's merge into the default branch does ("Freeze", under the changeset bullet).
  2. Cargo tools for implementing workers (sc-25877, E): `cargo build/check/test/fetch` in `ClaudeWorker.AllowedTools`, with the
     `dotnet` rules' residual ("Worker tools", under the taint bullet).
  3. A plan story merged outside the factory into its plan's changeset branch counts as merged for the plan (sc-25877, F4):
     `merged-into-changeset`; one merged into any other base stays failed ("Plan failures and controls").
  4. Every plan item's repositories must be configured (sc-25877, C): also a plan naming one repository ("Repository set").
  5. A blocker outside the plan is satisfied once the board shows it finished, without a factory merge (sc-25871,
     `PlanRelease.Satisfied`; "Dependency-aware release").
  6. Gate policy version 4: a changeset pull request has its own size limits, `risk.max_changeset_lines`/`max_changeset_files`
     (sc-25873; "Changeset size").
  7. The plan-time CI-trigger block: a repository whose workflows certainly cannot report on a plan's pull requests stops the
     plan before the planner runs, rather than warn (sc-25873; "CI that cannot run").
  8. `Factory:Repos` (sc-25877, round 2): a setting of its own for the repositories the factory may plan and work in besides the
     default one, so that decision 4 does not force issue triage on (`GitHub:Watch:Repos`) for a second repository
     (configuration table; "Repository set").
  9. A re-run of a plan story counted by `merged-into-changeset` does nothing (sc-25877, round 2; part of decision 3): before,
     `factory run` on it implemented the story a second time and opened a second pull request into the changeset branch, while
     the escalation said a re-run changes nothing ("Plan failures and controls").
  10. A plan story merged outside the factory into another base than its plan's changeset branch can be re-run (sc-25877, round
      2): the behaviour is unchanged — the re-run implements it again onto the changeset branch — and the hold comment now
      offers it instead of denying it; the outside merge on the other branch is left to the owner ("Plan failures and controls").
- Dependency-aware release (Phase 4a, sc-25871; `Plans/PlanRelease.cs`, `RunPipeline.Plan.cs` `PlannedAsync`/`ClosePlanAsync`): the
  orchestrator, not a person, releases a plan's stories. `RunPipeline.InFlightAsync` lists every `Planned` plan item (unless a
  control pauses it, or its closeout was given up on, below), so each intake poll — and a `factory run` on it — evaluates it
  through `HoldAsync` → `PlannedAsync`, under the item's run lock and with no worker: like every run outside its worker phases
  (sc-25988) it takes no sandbox lock, runs no helper (only the read of the installed helper every run starts with: a stale
  one fails it factory-wide like any run) and sweeps no worktree, so it is evaluated while another run's worker
  holds the sandbox user (`PlanRunnerLockTests`, against Postgres). A Stop it finds is finished by that run: the stop of a planner a
  crashed run left (sandboxed: a kill of every `_factory` process) is a worker phase, waited for.
  The plan's stories are the ids of its newest `plan-stories` row. Blockers read (cost rule): a story with no Merge row and no
  `plan-released` row is held, without a board read, while a blocker the plan itself gave it (`PlanRelease.RecordedBlockers`, from
  the `plan` checkpoint) has no Merge row; only once those have all merged — the evaluation that could release it — are its
  blockers read from the board (`IWorkSource.BlockersAsync`: Shortcut — the subjects of the story's `story_links` with `verb:
  blocks` and `object_id` the story, one GET for the story and one per blocker), so a relation a person added since is still seen
  before the release. Consequence: removing an in-plan relation on the board does not release a story early, and neither does
  moving it to ready by hand — the claim check holds it to the same recorded blockers (one rule, sc-25877; below). A `plan` checkpoint that does not read back as a plan of those stories skips
  nothing (every unreleased story is read). `PlanRelease.Decide` (pure) releases those whose blockers are all satisfied, and its
  `Complete` is the one definition of "every story merged" (`PlannedAsync` closes the plan on it).
  "Blocker satisfied" is decided in one place, `PlanRelease.Satisfied(history, ofPlan, finishedOnBoard)` (owner decision
  2026-10-12): a blocker that is a story of the plan is satisfied only by its ledger Merge row (a merge into whatever base its PR
  targets; a later story changes the base to a changeset branch there) — not one still in flight, one that escalated or was
  cancelled, nor one a person dragged to Done; a blocker that is not a story of the plan is satisfied by its Merge row or once the
  board shows it finished (`Blocker.Finished`: for Shortcut a workflow state whose `type` the API gives as `done` — never a state
  id or name — and not archived), since it is work the factory may never do. A release is `IWorkSource.MakeReadyAsync` (Shortcut:
  Backlog → `To Do`, state ids by name; typed result `ReadyResult`: `Moved`; `AlreadyLeft` — not in its workflow's Backlog,
  nothing written, so a claimed or finished story is never moved back; `Archived` — nothing written), then a `plan-released`
  checkpoint whose Detail starts with the story id (E4: board write, then the row; a run that died between the two asks again and
  the source writes nothing the second time; a story with a `plan-released` row is never released again). An archived story gets
  no `plan-released` row: it is asked for again every evaluation (unarchiving it releases it) and recorded as held. Before each
  release the plan item's effective control is read again: a paused factory, epic or plan item and the freeze row release nothing
  (and `RunAsync` defers on the freeze evaluator before any of it). A released story is an ordinary ready story: `ListReadyAsync`
  finds it if it is in the watch scope. One outside it (possible only when the plan item was run with `--ignore-scope`, since
  stories are created in the plan item's team and epic) is released all the same, its `plan-released` Detail and the log saying
  "outside the watch scope" and how to run it (`factory run sc-N --ignore-scope`); nothing lists it.
  What holds a plan beyond its own order is a `plan-held` checkpoint (outcome `deferred`), one row holding every such hold, written
  only when its text changes, not every poll: a story held by a blocker outside the plan (`sc-13 held by sc-99 (not a story of this
  plan, and neither merged by the factory nor finished on the board: finish it or remove the relation on the board)`); an archived
  story (`… is archived on the board, so it was not released and the plan cannot complete: unarchive it`; the two halves are the
  source's own words, `IWorkSource.Shelved` — Kanban: `Closed`, "move it back to Backlog"); and a released story a
  person moved back to the Backlog (`… was released and is back in the backlog: …; moving it to the board's ready state restarts
  it` — it is not released again: a person moved it back on purpose). The last is detected by `IWorkSource.InBacklogAsync`, asked
  once per evaluation only about a released, unmerged story that nothing works — the ledger does not know it, or its item is
  Paused; a story in flight is never read. The `[Planned] … waiting:` log line names the held stories, the archived ones and every
  released, unmerged story (`released, not merged` / `released, back in the backlog`). A plan story that escalates or is cancelled
  never satisfies its dependents: they stay in the Backlog and the plan item stays `Planned`, saying which story holds it (plan
  failures and controls, sc-25876, below). A board that cannot be read or written during an evaluation (a story deleted on the
  board, a 500) throws out of the run: nothing is recorded for that step, the intake loop shows the item's failure and polls again,
  and after `Intake:MaxItemFailures` in a row `RunPipeline.GiveUpAsync` (E10) comments once on the plan item (the reason fenced) and
  writes one `plan-held` row `not evaluated: <reason>`; the item stays `Planned`, is evaluated again every poll and goes on by
  itself once the cause is gone. The same reason is not commented again while that row is the item's last (a comment the board
  refuses is recorded `…; comment NOT posted: …` and posted by the next give-up).
  The plan link: a work item of a plan's story records its plan in `WorkItem.PlanItemId` (migration `PlanItems`: a nullable
  self-reference to the plan item's row, indexed), set once from the ledger alone (`WorkLedger.PlanOfAsync`: the plan item of this
  source whose newest matching `plan-stories` row lists exactly that id; never from the board's marker, which is text anyone may
  write or remove), looked up on each run until the item is first claimed (an item that has a `claimed` row and no link is no
  plan's story: its later runs do not scan the plans). Unfinished plan: a story whose description carries the planned-story marker
  but which the ledger links to no plan — its plan's run failed or died before `plan-stories` — is refused at claim
  (`RunPipeline.WithoutPlanAsync`): Paused `claim refused: its plan sc-N has not finished creating its stories: …` plus `parked`, no
  board write, and while the board keeps listing it nothing more is written (`StillWithoutPlanAsync`); once the plan's re-run has
  recorded `plan-stories` it is linked and held to its blockers like any plan story. The marker is only ever used to refuse (fail
  closed), never to grant: a story without it is a hand-written story. Blocked claim: Intake refuses the claim of a story with a
  `PlanItemId` while a blocker is unsatisfied by the same rule (`RunPipeline.BlockedAsync`: the blockers its plan recorded for it,
  `PlanRelease.RecordedBlockers` from the plan item's `plan` checkpoint — which hold it whatever the board says now, so a story
  whose `blocks` relation a person removed and that was moved to ready still waits for its recorded blocker's merge — united with
  the board's relations, `IWorkSource.BlockersAsync`, for one a person added since; which blockers are the plan's comes from
  the plan item's `plan-stories`, `WorkLedger.PlanStoriesAsync`), before the source is asked and with no board write, whoever moved
  the story to ready and also under `--ignore-scope`: Paused `claim refused: blocked by sc-12 (not merged): …` — for a blocker
  outside the plan ending `; sc-99: not a story of this plan, … finish it or remove the relation on the board` — plus the `parked`
  checkpoint, the same pattern as any refused claim. While the board keeps listing it, each run re-reads the blockers first
  (`StillBlockedAsync`) and, still blocked, returns the same refusal and writes nothing to the board or the ledger; once the
  blockers are satisfied the next run goes on (unpaused → Intake → claim). A blockers read that fails inside the claim check fails
  the run like any failed Intake step (no claim; Escalated, re-run when the board next lists it). Only a plan's stories are held to
  their blockers: a hand-written story's `blocks` relations stay the owner's own business, as before.
- Plan failures and controls (Phase 4a, sc-25876; `Plans/PlanRelease.cs` `Classify`, `RunPipeline.Plan.cs` `RecordFailedHoldAsync`,
  `Controls/ItemStopper.cs`, `Controls/Controls.cs` `LedgerControls.EffectiveAsync`, `Dashboard/DashboardData.cs` `PlansAsync`).
  Classification: every evaluation of a waiting plan classifies its stories from ledger rows alone (`PlanRelease.Classify`, pure;
  the dashboard calls the same function): `Merged` (`PlanRelease.Satisfied`: a Merge row, or a `merged-into-changeset` row — below),
  `Failed` (`PlanRelease.FailureOf`: not merged and the item's
  last transition is Escalated or Cancelled — `escalated`, `cancelled`, or `merged outside the factory` when the escalation reason
  is the review's "is merged outside the factory" or the merge queue's "was merged outside the gate" — `PlanRelease.MergedOutsideFactory`
  and `MergedOutsideGate`, the constants the two throw sites build their message from), `InFlight` (released, or
  known to the ledger and neither), `Blocked` (unreleased, and a failed story is among its unmerged plan blockers, directly or
  through others; `HeldBy` names the failed roots) and `Waiting` (unreleased, waiting on anything else or on nothing). A story a
  person re-queued after an escalation (Escalated → Intake) is in flight again, not failed; a story parked on a blocked claim still
  only waits. The release decision is unchanged and independent: a failed story has no Merge row, so `PlanRelease.Satisfied` already
  never releases its dependents, and the stories that do not depend on it are released and worked as before.
  A story merged outside the factory INTO ITS PLAN'S CHANGESET BRANCH counts (sc-25877; a coordinator decision pending the owner's
  confirmation, list below): when a plan story's run finds its pull request merged by someone else (`EnsureOpenAsync`, the merge
  queue's `RecordFoundMergedAsync`) and GitHub's answer — that run's own read through the gate client, never text — shows it
  merged into the base the ledger expects for it, the plan's changeset branch (`Run.BaseBranch`), the run records
  `merged-into-changeset` on the story (`RunPipeline.MergedIntoChangesetAsync`; Detail `<pull request> into <branch> at <head>`,
  once per pull request; typed outcome `deferred`: its own gate never judged it) before the Escalated row that still follows, whose
  reason then ends "It was merged into <branch>, its plan's changeset branch: merged outside the factory into the changeset
  branch; reviewed as part of the changeset pull request. For its plan it counts as merged …". From that row on
  `PlanRelease.Satisfied` holds for the story (`MergedOutsideChangeset`): its dependents are released, their claims go through,
  and the plan can complete — the work is on the changeset branch, and nothing reaches a default branch except through the
  changeset pull request's full panel and gate (E6 holds). It is said wherever the plan reports: the counts (`2 of 3 merged (sc-12
  merged outside the factory into the changeset branch; reviewed as part of the changeset pull request), …` in the hold and
  resume records and comments, `PlanRelease.Counts`), the dashboard's story row, the changeset pull request's body ("Not sc-12,
  which was merged outside …") and the plan's closeout ("1 of its 2 stories merged through the factory's merge gate. 1 more
  (sc-12) was merged outside …"), each story line saying it instead of "not merged". The story's item stays Escalated (the
  escalation metric counts it), a plan's Stop leaves it as it is (`ItemStopper.Settled`), and a re-run of it does nothing:
  `RunPipeline.RunAsync` leaves an Escalated plan story that `PlanRelease.MergedOutsideChangeset` holds for where it is (no row,
  no claim, no session; the run's error says why) instead of the usual Escalated → Intake re-run, which would implement the
  story a second time and open a second pull request into the changeset branch. The once-per-pull-request guard on the row is
  for a run interrupted between the row and the Escalated row (Ctrl-C, a crash): the run that resumes it finds the same merged
  pull request again. Since the item stays Escalated the factory never moves its board story, which stays In Progress and
  claimed: the escalation reason (so the escalation comment, rendered from the ledger) ends "… and a re-run of this story does
  nothing: its board story is still claimed by the factory: move it to Done by hand.", and the story's line in the changeset
  pull request's body and in the plan's closeout ends "; its board story is still claimed by the factory: move it to Done by
  hand" (`PlanRelease.BoardStoryStillClaimed`; code-written words, the planner's title stays in its code span). A
  pull request merged outside the factory into ANY OTHER base (retargeted to the default branch, say: past the changeset, ungated)
  writes no such row: that story stays failed — `merged outside the factory` — until it is re-run or its plan is stopped. A re-run
  is a remedy (sc-25877, round 2; before, the hold comment wrongly said it "cannot be re-run"): `factory run <story>` takes the
  Escalated item back to Intake like any re-run, implements the story again from the plan's changeset branch and opens a second
  pull request into that branch, which the gate merges — a Merge row, so the story is merged for its plan and its dependents
  are released. The hold comment says so apart from the other failed stories (`PlanRelease.FailedRemedy`: "… can be re-run too
  (`factory run sc-12`): the re-run implements it again onto the plan's changeset branch through the gate … Its earlier pull
  request is not part of the changeset, and what that put on the other branch is yours to deal with"); only a cancelled story
  cannot be brought back by a re-run. A story of a plan with no changeset (recorded before
  changesets) has the default branch as its base and never counts this way.
  Held by a failed story: when the record changes — never every poll — the plan item gets one row: `plan-held` whose Detail starts
  `held by a failed story: ` (`PlanRelease.FailedPrefix`; told apart from the outside-blocker and `not evaluated:` `plan-held` rows
  by that prefix) then, per failed story, `sc-12 escalated at <utc>: \`<reason>\`` — the reason from the story's own Escalated or
  Cancelled row, cut at `PlanRelease.MaxFailureReason` (300) and made one inert code span (`UntrustedText.CodeSpan`) — and
  `; not released because of them: sc-13 (behind sc-12), …`; or `plan-stuck` with the same Detail once the plan is stuck
  (`PlanStatus.Stuck`: every story that has not merged is failed or blocked behind one). Both have outcome `deferred`: the plan
  item never escalates for a story's failure, stays `Planned` and polled, and adds nothing to the freeze's consecutive-failures
  trigger beyond the story's own Escalated row. The record changes when the failed stories do, when one fails again (the time is in
  it), when what they block does, or when held becomes stuck. Once no story is failed any more, one `plan-resumed` row (outcome
  passed) lifts it; the dependents are then released as their blockers merge, by the ordinary rule. Each such row gets ONE comment
  on the plan item through `IWorkSource.CommentAsync`, after the row (E4): `PlanRelease.HeldComment` (failed stories with state,
  time and reason; the stories blocked behind them; the counts; both remedies — fix and `factory run sc-N`, or
  `factory stop --item <plan>`; a cancelled story cannot be re-run) or `ResumedComment`. Its result is a `plan-notice` row (`posted`,
  or `failed: …` with outcome failed); a row with no posted notice is tried again by the next evaluations up to
  `RunPipeline.MaxPlanNoticeAttempts` (3) and then left to the ledger and the dashboard, whose plan row says `hold comment not posted
  (attempt n of 3; tried again on the next poll | not tried again: …)` from those rows (`PlanView.FailedNotices`); a posted one is
  never repeated (a run that died between row and comment posts it on the next evaluation). Hold bookkeeping runs before the completion check and does not
  depend on how a complete plan is finished.
  Plan-scope controls use NO new scope kind: a plan is controlled at its own `item:<plan id>` scope (`factory pause|continue|stop
  --item <plan>`, the dashboard's control form on the plan row), and `LedgerControls.EffectiveAsync` also consults, for an item with
  a `PlanItemId`, its plan item's row — inside its one query (the plan's scope comes from a self-join on the ledger's
  `(Source, ExternalId)` index; a second query, for the story's Merge row, runs only while the plan's row is Stopping), since it is
  read every second for every running worker and test run; a read that fails throws, so a failed plan lookup is an unreadable
  control (a Pause after `Controls:MaxReadFailures`, E2), never "no plan" — so every existing consumer (the pipeline's step check, `ControlWatch`, the gate's test
  watch, `InFlightAsync`, the merge queue's `HeldIdleByControlAsync`) honours it unchanged. Pause on a plan: its row pauses the
  plan item (nothing released: a paused plan item is not evaluated) and every story of it — an in-flight one stops at its next tool
  boundary, Paused `user-paused` (`UserPausedAsync` counts the plan's row as a user pause), auto-resumed on Continue; a released
  story the ledger does not know yet is not claimed (`RunPipeline.PlanControlAsync`, from `WorkLedger.PlanOfAsync`). Continue lifts
  it. Stop on a plan: `EffectiveAsync` answers Stopping for each story of it that has no Merge row (a merged story is untouched —
  for it the plan's Stop is nothing, though a plan's Pause still pauses its bookkeeping), `InFlightAsync` lists the plan item and
  those stories as being stopped, plan first, and `ItemStopper.StopLockedAsync` on a plan item (one whose ledger has `plan-stories`;
  the same path whether the CLI, the dashboard or the plan's own run calls it, and whether or not the plan item has a pull request
  of its own) stops the stories first (`StopStoriesAsync`), each under its own run lock and on its own rows by the ordinary item
  stop (`prs-drafted`, board Stopped + comment `… was stopped by <who> (with its plan sc-N)`, `stop-reported`, Cancelled): a story
  whose run holds its lock is left to that run (which sees the Stop within a second), one whose worker a crashed run may have left
  to its own next run (which stops the worker first, under the sandbox user's lock), and one whose stop failed to the next attempt — all
  "pending". A story the plan released that the ledger does not know was never claimed: the factory did no work on it, and the
  board is asked before anything is written (through `IWorkSource`, E6). Still as the release left it — not archived, in the ready
  state, no owner or claim but the factory's — it is moved back to the Backlog with a comment that says its plan was stopped before
  the factory started it (`IWorkSource.ReturnUnstartedAsync`, which re-reads and writes only then; `PlanRelease.ReturnedComment`:
  never "cancelled its work" or "released its claim"), and then gets a ledger item recorded Intake (`PlanRelease.ReturnedUnstarted`)
  → Cancelled, with no `prs-drafted`/`stop-reported` rows; the board is written first, so an attempt cut short finds it in the
  Backlog (or its item at Intake, which is then only recorded Cancelled). One that a person took (another owner, In Progress),
  finished (Done), assigned to themselves or archived since its release is theirs: nothing is written to it — no move, no comment,
  no ledger item — and the plan's comment lists it as left as it is. One never released, or back in the Backlog
  (`IWorkSource.InBacklogAsync`), is left with nothing written. A story Escalated because its pull request was merged outside the
  factory (`PlanRelease.AsMergedOutside`) has its change on the base branch: it is not stopped, drafted, moved or commented — its
  item stays Escalated — whether the plan's stop or the story's own run (listed as being stopped while the plan's row is Stopping)
  reaches it (`ItemStopper.Settled`). While any story is pending the stop
  throws `StopPendingException`: the plan item is unchanged, keeps its Stopping row, `ControlActions.StopAsync` answers not Ok (the
  CLI exits 1) naming each pending story, and later polls finish it. Only then is the plan item itself stopped like any item —
  its own pull requests back to draft, board Stopped with one comment listing `Stories stopped with it: …. Already merged (left as
  they are): …. Never started (left in the backlog; the factory will not release or start them): ….` and, only when there are
  any, `Released but never started (moved back to the backlog; the factory did no work on them): ….`, `Merged outside the factory
  (left as they are: their change is on the base branch): ….` and `Taken, finished or archived by someone else since their release
  (left as they are): ….`, Cancelled, its control cleared. A waiting plan item's run only evaluates its stories and watches no control meanwhile, so: a Stop that lands during
  the evaluation is seen when it ends (`HoldAsync` re-reads the control after `PlannedAsync`) and finished by that run under the
  run lock it holds; and `factory stop --item <plan>` while a run of the
  plan holds its lock answers `stop NOT finished: it is a plan and a run of it is active …`, not Ok (exit 1), instead of the
  ordinary item's "is running; its run stops it at once" (exit 0), which stays true only for a run that watches its control. After the stop, a story of the plan
  that reaches the ready state anyway (moved by hand) is refused at claim, once and with no board write: Paused
  `claim refused: blocked by its plan sc-N, which was stopped: …` (`PlanRelease.PlanStoppedRefusal`; `BlockedAsync` checks the plan
  item's state first). A Pause or Stop on one story of a plan works as on any item; a stopped story is then a failed story.
  Lifecycle: no table row was added — `Planned → Cancelled` (via the Stopping control) and `Planned → Paused` were already legal by
  the general rules; a paused plan item is simply not evaluated and never needs a Paused row.
  Dashboard: `PipelineRow.Plan` (`PlanView`) is read from the ledger alone — the plan item's `plan`, `plan-stories`, `plan-released`
  and `plan-held` rows and its stories' items and transitions; the board is never asked — and `PipelineRow.PlanItemId` marks a
  story of a listed plan. A finished plan item (Done, Cancelled) is still listed — a row that says so and offers no control
  (`PipelineRow.Finished`) — for as long as one of its stories is listed (merged stories stay in Watch), so they stay grouped
  beneath it instead of becoming rows of their own (sc-25877). The pipeline page lists a plan's stories beneath the plan row, in plan order (`tr.plan-story`,
  `data-plan`, `data-story`): one with a work item as its full row (state, PR, sessions, its own controls), one without (not
  released, not picked up, or finished) from `plan-stories` with the title its `plan` checkpoint recorded, made inert
  (`UntrustedText.Inert`, and Razor encodes it); each with its state and what it waits on (`PlanStoryView.Waits`: `waits on sc-12`,
  `held by sc-12 (escalated)`, `failed: escalated`). The plan row shows `m of n merged · i in flight · w waiting · f failed`,
  `held`/`stuck`/`stopping`, the failed-story hold (computed from the ledger, so never stale) and the last other `plan-held` row
  recorded. Story rows show the control their plan puts on them. The refresh is unchanged (`NOTIFY work_items` → `PipelineChanges`).
  A plan's story the ledger knows but the factory never claimed (parked on a refused claim, e.g. `claim refused: blocked by …`;
  no `claimed` row) is stopped — by its own Stop or its plan's — with a comment that says so (`PlanRelease.NeverClaimedComment`:
  "The factory did no work on it"), never "cancelled its work … released its claim".
  Closeout of a plan with no changeset to merge (a plan recorded before changeset branches, or an empty changeset — otherwise see
  the changeset bullet below): once every story of the plan has a Merge row, the plan item is reported `BoardState.Merged` (`merged-reported`, Detail
  `n stories merged`), gets one comment rendered from the ledger (`LedgerReport.PlanCloseout`: per story its id, its title inert in
  a code span, its last transition's typed outcome and state, the merge commit of its Merge row and the PR of its `linked`
  checkpoint — an https URL as is, anything else as a code span; at most `MaxPlanStoriesShown` (40) stories, the rest counted;
  `closeout` `posted`), and goes `Planned → Done` (terminal; Detail `n stories merged`), each step a row before the next. A closeout
  the board refuses is recorded `failed: …` (outcome failed) and tried again by the next runs, shown on the dashboard like a merged
  item's, up to `RunPipeline.MaxCloseoutAttempts` (3). After the last the item stays `Planned`, as a merged item stays in Watch:
  no poll lists it any more (`RunPipeline.PlanCloseoutAbandoned`), a run by hand tries and writes nothing, and the dashboard row
  keeps saying `not retried again: post it by hand`; it reaches Done only by a posted closeout. A run on a finished plan item
  changes nothing (no held notice). As for every closeout, a run that dies after the comment is accepted and before its `closeout`
  row posts it once more.
  GitHub issues (E5): `GitHubIssueWorkSource.BlockersAsync` answers none, `InBacklogAsync` false, and `MakeReadyAsync` throws
  `NotSupportedException`, like its `CreateChildrenAsync` (the interface defaults do the same for any source that creates no
  children).
- Changeset branch (Phase 4a, sc-25872, owner decision 2026-10-12: a multi-PR change set is built on a feature branch — its
  stories merge to it independently and it is then merged as a whole; no atomicity; `Plans/Changeset.cs`, `RunPipeline.Plan.cs`
  `EnsureChangesetAsync`/`ChangesetPullRequestAsync`). One changeset per plan and repository (which repository is a parameter of
  everything here; today every story of a plan is in the plan item's repository).
  Branch: `factory/changeset-<plan item>` (`ItemNaming.ChangesetBranchName`, e.g. `factory/changeset-sc-123`; under `factory/`
  because the rulesets let the factory App write nowhere else). Created in Plan, after the stories are on the board and before the
  `plan-stories` row that makes them the plan's (no story is claimed without that row, `WithoutPlanAsync`, so none can start before
  its base exists): `IRepoWorkspace.BranchStartAsync` reads the default branch and its head (and the branch's head if origin has
  it); the `changeset` checkpoint (`Changeset` JSON: repo, branch, base, base_sha) is written first; then
  `IRepoWorkspace.CreateBranchAsync` pushes that commit to the new ref from the clone — the orchestrator, the factory App's token,
  `GitRemoteWrites.CreateBranch` (`push origin <sha>:refs/heads/<branch>`, never forced), no worktree, no worker, no `PushGrant`
  (it publishes no worker work) — then `changeset-pushed` (`<branch> at <sha>`, `(adopted: origin already had it)` when so).
  Idempotent: a recorded changeset is pushed from its recorded commit by a run resumed before the push; a branch origin already
  has is adopted as it is, never reset; one recorded as pushed is left alone. A plan's changeset is the newest `changeset` row
  for the repository that a `changeset-pushed` row follows (`Changeset.Of`, `WorkLedger.ChangesetOfAsync`).
  Story base: an item whose plan (`WorkItem.PlanItemId`) has a changeset in its repository branches from it and opens its pull
  request against it (`Run.BaseBranch`, read from the plan item's ledger each run; `IRepoWorkspace.PrepareAsync`/`RestoreAsync`/
  `ReopenAsync` take the base, null = the default branch; `GitWorkspace` refuses a base that is not a `factory/changeset-*`
  branch on origin). Fail closed on the repository: a story whose plan recorded a changeset (any `changeset` row) but has none
  pushed in the story's own repository — its `Repo:` line was edited to another one — escalates before any step
  (`WorkerFailedException` naming the plan's changeset branch and both repositories; no worktree, no PR), rather than merge into
  that repository's default branch, count as merged and miss the changeset; only a plan with no `changeset` row at all (recorded
  before changesets) sends its stories to the default branch. Fail closed on the PR's base (`RunPipeline.EnsureBase`, in Review,
  at the top of every merge-queue pass and right before the merge): the PR must target the base the ledger expects — the plan's
  changeset branch for a plan's story, the changeset's recorded base for a plan item, else the repository's default branch as
  GitHub names it in the same pull answer (`PullFacts.DefaultBranch`, `base.repo.default_branch`: no extra call; an answer naming
  none is not checked) — otherwise the item escalates `<url> targets <base>, not <expected>` with nothing reviewed, updated or
  merged (a PR can be retargeted by a person or by GitHub when its base branch is deleted, and `OpenAsync` adopts an open PR of
  the head whatever its base). Everything downstream already reads the PR's own base (`PullFacts.BaseRef`/`BaseSha`): the review's diff and
  file list, `factory/gate.yaml` at the PR's base commit (the changeset branch carries the default branch's; the path is sealed, so
  no story can change it there), CI and its base-CI triage, the new-tests base/head runs, the merge queue's compare, base update
  (`MergeBaseAsync` merges `origin/<Workspace.BaseBranch>`) and conflict fix rounds, `merge-files`. So a plan story's PR gets the
  full panel and gate, no weaker for the changeset's own review later (E6). Its closeout says `merged into its plan's changeset
  branch …`, its PR body `Base: …`. An item of no plan, and a story of a plan with no changeset, targets the default branch,
  unchanged. Blocker satisfied is still the blocker's Merge row (`PlanRelease.Satisfied`): merged into the changeset branch, so a
  dependent story's worktree holds its blockers' code.
  Merge queue: stays one queue per repository (not per repository and base): stories of one plan must serialise their merges into
  the changeset branch, which one queue gives; a queue per base would only let a plan's stories and default-branch items merge at
  once — which, with several runs and worker slots at once (sc-25988, sc-25989), would let a plan's stories and default-branch
  items of one repository merge at the same time. One queue per repository is kept deliberately (sc-25992): merges into one
  repository stay one at a time whatever their base, and the ledger-derived order and the repo-keyed lock stay as they are.
  Changeset pull request: when `PlanRelease.Decide` is `Complete`, `PlannedAsync` (the waiting plan's run, GitHub's API only, no
  worker) reads GitHub's compare of the default branch and the changeset branch. Empty (no difference): `changeset-empty`, no pull
  request, and the plan is closed out as above saying so (`Planned → Done`). Before that compare, the plan item's newest `linked`
  pull request is read: found merged (a person merged an escalated changeset PR by hand — after a squash or rebase GitHub's
  three-dot compare still shows a difference, and every re-run would open another PR), it is recorded the same way
  (`changeset-empty`, Detail `changeset pull request <url> was merged outside the factory`, `Changeset.MergedOutside`) and the plan
  is closed out saying the factory's gate did not merge its changeset. A changeset PR found merged while the item is in Review, CI
  or MergeGate (no `gate-passed` for that head) escalates once as any item's does ("merged outside the factory"); the owner's
  re-run then takes this path to Done. Otherwise it opens ONE pull request from the
  changeset branch into the default branch (`IPullRequests.OpenAsync` finds an open one rather than opening a second; a control
  that holds the plan item, checked again after the compare, opens nothing; title — the plan item's title through
  `UntrustedText.Inert` — and body written by code, `LedgerReport.ChangesetPullRequestBody`: the stories from their own ledger rows, planner titles inert),
  links it (`linked`) and records `Planned → Review` — the plan item itself is the work item of the changeset, with the changeset
  branch as its branch (`Run.Branch`/`Run.Changeset`) and no Implement. From there the existing handlers drive it like any item:
  the full panel on the whole diff (spec for the reviewers: the plan item's own text, then the plan's story list in a
  `<plan-stories>` fence as a planning model's data, `Changeset.ReviewSpec`), CI and CI self-heal, the merge gate at the default
  branch's policy (every tier check, `new-tests-fail-on-base` included: the changeset's new tests are the union of its stories',
  each must fail on the default-branch base and pass on the changeset head, xUnit and cargo alike; the `risk` size limits apply
  to the whole changeset — under a version 4 policy its own `max_changeset_lines`/`max_changeset_files`, under versions 2 and 3 the
  ordinary `max_changed_lines`/`max_changed_files`, which must then be sized for a plan or the gate escalates it; "Changeset size"
  below),
  CodeGraph risk (unchanged for both kinds of PR: blast radius is read at the PR head through its overlay, the hotspot from the
  default-branch index by path — a file that exists only on the changeset branch has no metrics and counts 0, like any new
  file), the merge queue's turn against the default branch (a changeset behind it is updated from it, fast-forward push), fix
  rounds — a fixer in a worktree of the changeset branch (`factory-changeset-<item>`, kept by the sweep while resumable), its push
  going to the changeset branch, which `GitWorkspace.CommitAndPushAsync` never forces (`ItemNaming.IsChangesetBranch`: a push that
  would lose a merged story fails) — on a `PushGrant` for the fixer's sessions (the plan item's planner session is tainted and is
  not in a fix round's attempt), typed outcomes, Stop (`ItemStopper` drafts the changeset branch's pull request — the plan item
  has no `factory/<id>` branch — after stopping the plan's unfinished stories, and `StopIdleAsync` removes the changeset
  worktree; the branch stays), escalation. Fix rounds are counted since the item last entered Planned
  (`TransitionContext.StartsAttempt`: Implement or Planned), so a re-run starts the count again as an Implement does. Merge
  (`MergeAsync`): the plan item is reported `BoardState.Merged` and gets its closeout (`LedgerReport.PlanCloseout` with the
  changeset's merge, each story, then the plan item's own facts) only now, then Watch. If the changeset PR escalates, the plan
  item is Escalated with the reason and the usual comment; its stories stay merged on the changeset branch. A re-run
  (Escalated → Intake → Plan → Planned, no planner, no new story or branch) finds the same pull request, and a failed verdict
  recorded for its head before the re-run is not reused (there is no Implement to make a new head): the panel reviews it again.
  Freeze: hot-file counts only merges into a branch that is not a changeset branch (`!ItemNaming.IsChangesetBranch(base)`, as
  main-red reads), per repository, base branch and file — the trigger's meaning before plans: factory merges changing one file of
  a default branch. A plan's story merges into its changeset branch never count (chained stories building on one file there are
  the plan working as designed, however many there are); the changeset pull request's own merge into the default branch counts,
  with every file of the changeset, so changeset merges of different plans (or ordinary items) changing one file within the
  window still freeze. A coordinator decision of 2026-10-13 (sc-25877), pending the owner's confirmation (list below); main-red skips merges into a changeset branch before picking each repository's latest merge, so a red changeset branch
  freezes nothing (the next story's CI triage finds its base red and escalates that story; the changeset PR needs green CI on its
  own head) and a later story merge never hides the default branch's state; cost-rising reads a plan item's rounds since Planned.
  Changeset size (sc-25873): `risk.max_changed_lines`/`max_changed_files` are sized for one story, and a changeset PR is the sum
  of a plan's stories. The limit is never skipped or loosened in code for a changeset (what is on a changeset branch — adopted
  branches, fix-round and base-update commits — did not all pass a story's gate); the policy sets a separate one: `version: 4`
  requires `risk.max_changeset_lines` and `max_changeset_files` (whole numbers ≥ 1, independent of the per-story two — no rule ties
  them, since a smaller changeset limit only tightens the gate, which a policy may always do). `risk-threshold` holds a changeset
  PR to those two (`RiskThreshold.LinesLimit`/`FilesLimit`) and every other PR — a plan's story into its changeset branch included
  — to `max_changed_*`; under versions 2 and 3 (no changeset keys: unknown there, and invalid) a changeset PR is held to
  `max_changed_*` as before, and its reason says so. Fix rounds, CodeGraph's blast radius floor and hotspot limit, and every other
  check are the same for both kinds. Which PR is a changeset's is an input fact from the ledger, `ChangeFacts.Changeset`
  (`RunPipeline.IsChangesetPull`: the item has a pushed changeset in the repository by its own `changeset`/`changeset-pushed` rows
  — `Run.Changeset` — and the PR targets the base that row recorded), never the branch's name, which an adopted or worker-chosen
  name could imitate; the `gate` checkpoint's Detail records it (`Merge <sha> (changeset pull request): …`,
  `GateDecision.ChangesetMark`), a failure names the limit that applied (`… exceed max_changeset_lines N (a changeset pull
  request)`) and a pass both (`N changed lines within max_changeset_lines M, …`). This repository has no `factory/gate.yaml` of its
  own; a target repository's policy moves to version 4 by an owner's PR (the path is sealed).
  CI that cannot run (sc-25873, `Gate/CiTriggers.cs`, `RunPipeline.EnsureCiTriggersAsync`): a target repository whose workflows
  filter `pull_request` `branches` without `factory/**` reports no check for story PRs (their base is `factory/changeset-*`), which
  would each wait out `Gate:CiTimeoutMinutes` and escalate — fail closed, and a whole plan wasted. So Plan, before the planner
  session (and before a recorded plan's stories are created; not once the repository's changeset branch is on origin), reads the
  default branch's name and workflow files owner-side with the gate App's read-only token (`IGateGitHub.GetWorkflowsAsync` →
  `RepoWorkflows`: `GET /repos/<owner>/<name>` for `default_branch` — never guessed; an answer naming none throws — then the
  contents API with no ref, every `.yml`/`.yaml` directly in `.github/workflows`, at most `GitHubGate.MaxWorkflows` 50 files of at
  most `MaxWorkflowBytes` 512 KiB) and parses each `on:` with the policy's YAML parser. `EnsureCiTriggersAsync` takes the
  repository as a parameter and `PlanAsync` calls it for every repository the plan item names (`PlanRepositories`, the planner's
  set — every `Repo:` line, the item's own first; one call for a plan of one repository), in that order, all before the planner
  session: the first repository that blocks stops the plan (its reason starts with that repository), so no planner session, story
  or changeset branch is spent in any repository; a warning is a row per repository (compared with that repository's last
  `ci-triggers` row only, `CiTriggerCheck.IsAbout`); the "changeset branch on origin" skip is per repository too. A repository the
  plan item names is checked even if the plan then puts no story in it (not known before the planner runs): a block for a plan
  item of several repositories says so and names both ways out ("remove its `Repo:` line from the plan item if the plan should not
  touch it, or fix the workflow …"; on a run continuing a recorded plan it says whether that plan has a story there). A stop says
  what the ledger says an earlier run left (`PlanProgress`, also for `PlanRepositories`' "not configured" refusal): only a run
  that plans afresh says "before it was planned or any story of it created" / "no story was created"; one continuing a recorded
  plan with a `plan-creating` row says stories may already be on the board and are not released and cannot start (they are not
  the plan's until `plan-stories`). `CiTriggers.Check(files, base, head)` is asked about both kinds of PR the plan opens there: a story's
  (base the changeset branch, head the story's `factory/<id>` branch) and the changeset's (base the default branch, head the
  changeset branch — a repository whose only trigger is `pull_request: branches: ['factory/**']` runs for every story and never
  for the PR that delivers the plan). A workflow reports on a PR when a `pull_request`/`pull_request_target` trigger's branch
  filter matches the base (no filter matches every base) or a `push` trigger's matches the head (a push workflow's checks are on
  the commit the gate reads CI for; a push with only `tags` filters does not run for a branch); `types`/`paths` filters are not
  read (such a trigger counts as running). Branch filters follow GitHub's rules (`CiTriggers.Matches`: `*` stops at `/`, `**` does
  not, `?`/`+` repeat the character before, `[…]` sets, `\` escapes, patterns in order with `!` excluding again, `branches-ignore`
  the opposite and without `!`; case-sensitive; translated to a non-backtracking regex). Outcomes, per PR kind: a workflow runs →
  a log line only. Certain that none can — `Never`: every workflow file read and parsed, each made only of pull request/push
  triggers that do not match and events in `CiTriggers.NeverReports` (e.g. `schedule`, `workflow_dispatch`, `workflow_call`), AND
  at least one of them a pull request or push trigger whose `branches`/`branches-ignore` filter leaves the branch out (the evidence
  that the repository's CI is GitHub Actions and was told not to run there). It cannot be told — `Unknown`: no workflow file at
  all, or none with a pull request trigger or a push trigger for branches (only `workflow_dispatch`, a schedule, a release,
  `push` for tags: CI may be another service's, and "add a pull_request trigger" would be the wrong fix), a file unreadable, over
  the bounds or not valid YAML, `branches` beside `branches-ignore`, a pattern that cannot be translated, an event outside the
  known lists such as `workflow_run`, GitHub not answering the repository or the listing. A `Never` for either kind (the stories'
  first) → BLOCK: a `ci-triggers` checkpoint (`blocked: <repo>: …`, outcome gate_rejected) and the plan item escalates with the
  repository, the fix, then the workflow files read with why — no planner session, story or changeset branch; the owner fixes the
  workflow on the default branch and re-runs the plan item. Blocking is chosen over a warning because every story would otherwise
  be guaranteed to time out, or — the changeset PR — every story be built and the plan then never delivered. The reason puts the
  fix before the file list and bounds both (the escalation comment clips a reason at `LedgerReport.MaxReasonLength` 4,000): at
  most `CiTriggers.MaxFixes` (3) workflows' fixes, any one being enough, then "(or likewise in N more)", and at most
  `CiTriggers.MaxListed` (10) files, those that are why first, then "… N more". The fix names the pattern the base needs:
  ``add `factory/**` to the pull_request branches filter of <file>`` for story PRs, ``add `<default branch>` …`` for the changeset
  PR (a workflow's text is never repeated; an unusual file or default-branch name is not shown). Else an `Unknown` for either →
  a `ci-triggers` checkpoint (`warning: <repo>: …`, outcome failed, written when it changes) and a `[plan] warning:` log line, and
  planning goes on. Nothing is weakened either way: the gate still needs green CI on every head. No start-up warning: `factory run`
  and `factory work` read nothing from GitHub at start-up, and one per configured repository would add a GitHub dependency that
  could fail start-up; the plan-time check is the only one. A pipeline with no gate stage (tests only) does not check.
  Hot-file and a plan's stories (the rule above; a coordinator decision of 2026-10-13, sc-25877, pending the owner's confirmation):
  story merges into a changeset branch are not hot-file merges, so `Freeze:HotFileMerges` or more stories of one plan changing the
  same file do not freeze the factory. What the trigger no longer sees: one file fought over inside a single plan — that plan's
  changeset pull request, its size limits and the fix-round cap are what bound it.
  Cleanup: the factory never deletes a changeset branch (after the merge it is fully contained in the default branch; GitHub
  deletes it when the repository's "automatically delete head branches" is on).
- Multi-repository plans (Phase 4a, sc-25873; owner decisions: a multi-repository change is one work item per repository — every
  story stays a one-repository item — grouped by the plan; the planner is the agent that sees every repository; each repository's
  changeset is built on its own branch and merged to that repository's default branch as a whole, one repository at a time in the
  plan's order; merges across repositories are NOT atomic and nothing is rolled back).
  Schema (`Plans/Plan.cs`, `PlanParser.Parse(answer, repos)`; `repos` = the plan item's `Repo:` set): each story has `repo`
  (`owner/name`, one of the set, matched case-insensitively and recorded as the item spells it; required when the set has several;
  with one it is optional, may only name that one, and is recorded as none — a one-repository plan's `plan` checkpoint is
  byte-for-byte what it was); the plan has `merge_order` (the repositories that have a story, once each, in the order their
  changesets merge; required once stories are in more than one; a listed repository no story uses is dropped: it gets no
  changeset), `interactions` (the planner's note on how the repositories interact, ≤ `PlanParser.MaxInteractions` 1,500 chars,
  cleaned like every planner text, required with more than one repository; untrusted, E3) and per story
  `blocked_by_changesets` (repositories of the plan whose changeset must be on its default branch before the story starts, e.g. a
  dependency pin). Refused (`plan-rejected`, escalated, nothing created): an unknown repository anywhere, a story with no
  repository in a several-repository plan, a repository in `merge_order` twice or one with a story missing from it, a missing
  `merge_order`/`interactions`, a story blocked by its own repository's changeset (it could never start), by one that merges later
  than its own, or by one with no story, and any cycle across the two kinds (`Ordered`: a story blocked by a changeset comes after
  every story of that repository). The planner prompt (`PlanPrompt.Build(…, repos)`) explains all of it only when the set has several.
  Stories (`Plan.Children`): the code-written `Repo:` line (first, outside every fence) names the story's own repository; a
  second tilde fence (`Plan.Context`) carries the plan's repositories, the merge order, what the story waits on and the note —
  planner-authored stories are fenced as data in every worker prompt, so the implementer sees it as data. Story blockers stay
  board `blocks` links; a changeset blocker has no board relation — the `plan` checkpoint and that fence are its record.
  Changesets: `PlanAsync` creates `factory/changeset-<plan item>` in every repository of `Plan.Repositories` (merge order), each
  with its own `changeset`/`changeset-pushed` rows, all before `plan-stories`; a failure for one fails the step and a re-run
  creates only the missing ones (`EnsureChangesetAsync` per repository). `Changeset.Of(history, repo)` is per repository; a story's
  base is its own repository's changeset (`ledger.ChangesetOfAsync(plan, repo)`); `Changeset.All`/`Ordered` list them.
  Release (`PlannedAsync`, `PlanRelease`): a changeset blocker is a blocker id (`PlanRelease.ChangesetBlocker(repo)`: "the
  changeset of owner/name"), satisfied by the plan item's `changeset-delivered` row for that repository
  (`Changeset.Delivered`); `Decide` never counts it an outsider; `Classify` treats an undelivered one as a dependency on every
  unmerged story of its repository, so a story behind a failed one through a changeset is Blocked and `plan-held`/`plan-stuck` and
  the comment say "behind sc-N, through the changeset of owner/name"; the claim check (`BlockedAsync` → `AwaitedChangesetsAsync`,
  from the plan item's ledger) refuses a hand-moved story naming the changeset. Fail closed: the `plan` checkpoint is the only
  record of changeset blockers, so when a plan item with more than one changeset has a `plan` row that no longer reads back
  (`PlanRelease.RecordedRepos` null) the evaluation releases nothing (`plan-held`: `PlanRelease.HeldUnreadable`, written once) and
  every story's claim is refused (`PlanRelease.PlanUnreadableRefusal`, a blocker-style refusal: parked once). A plan story's
  repository at run time is its board `Repo:` line; `RunAsync` compares it with the repository the plan recorded for that story
  (`RecordedRepoAsync`) and a mismatch escalates the story before any work ("recorded it in A, but its `Repo:` line now names B"),
  as a story naming a repository with no changeset of the plan already did — it never merges into another repository's changeset.
  Ordered changeset pull requests — design (i), the plan item drives them in turn: with several changesets the plan item takes
  the first undelivered repository's changeset (in `Changeset.Ordered`) to its default branch once every story of that repository
  has merged (after that evaluation's releases), exactly as a one-repository plan does — Planned → Review → CI → MergeGate →
  Merge in THAT repository (`ChangesetPullRequestAsync` sets `WorkItem.Repo` to the changeset's repository; `RunAsync` resolves a
  plan item's repository from `Changeset.PassRepo` — its last `linked` pull request's repository, in the spelling its `changeset`
  row recorded (the plan item's `Repo:` line, like its stories'; not the URL's case), while its state, or the state it paused
  from, is a gate state — so the gate policy, CI, CodeGraph risk, new-tests check, merge queue and worktrees are that
  repository's). `PassRepo` is decided from the rows read UNDER the item's run lock, never from what was loaded before it (another
  process may have finished one pass and opened the next meanwhile). `WorkItem.Repo` rule: it is the pass's repository from
  Planned → Review until the pass ends — `MergeAsync` restores the plan item's primary repository (`Run.Primary`: the first
  `Repo:` line) right before Merge → Planned and before the final Merge → Watch (`EndPassAsync`; a run that dies between the two
  writes finds the item in Merge, where `PassRepo` names the pass again), and a pass that escalated keeps the pass's repository
  until the item is re-run (the run's start restores the primary) or stopped. So outside a pass — Plan, Planned, Watch, Done —
  it is the primary repository; whatever must know where a merge went reads the rows (`MergeFiles.Repo`, `changeset-delivered`,
  `linked`). Every item, plan or not: `ReadPullAsync` refuses (escalates, nothing read or merged) a linked pull request whose
  URL names another repository than the run's — a number alone names a different pull request there. Residual, accepted (sc-25877):
  a repository renamed or transferred on GitHub trips the same guard — GitHub answers the old name by redirect, a pull request
  opened since carries the new name in its URL, and the guard cannot tell that from a wrong repository — so the escalation ends
  "If the repository was renamed or transferred on GitHub, update the item's `Repo:` line / `Factory:DefaultRepo` to the new
  name and re-run." (`RunPipeline.RenamedRemedy`); nothing follows the redirect by itself. Then `MergeAsync` records `changeset-delivered` (`ChangesetDelivery`: repo, how — `merged` with pull request and
  commit, `empty`, `merged-outside`), rewrites that pull request's description and, while another changeset is left, records
  Merge → Planned (the one new table row; Planned starts an attempt, so fix rounds restart) instead of reporting the plan merged.
  The last merge takes the old path (merged-reported, closeout, pr-report, Watch). A plan with ONE changeset writes none of the
  new rows and behaves exactly as before. Why (i) and not a ledger item per changeset: every board write of the pipeline is keyed
  by the item's board id and the ledger already scoped a plan item's attempt by Planned (`TransitionContext.StartsAttempt`);
  per-changeset items with no board item would need a second identity through intake, controls, the stopper and the dashboard.
  What keeps passes apart (checkpoints carry no repository): verdicts, overlays and new-tests results are bound to
  commit SHAs; `gate-passed` is bound to its head SHA AND read per pass (a pull request found merged counts only when a
  `gate-passed` of `Changeset.CurrentPass` names that head, `GatePassedAt`: an earlier repository's gate pass never vouches for
  this one's); fix rounds, stuck rounds and cost rounds restart at Planned; the queue's `queued`/`queue-turn` are read per
  MergeGate stint; `linked` and the item's pull request are the last one; `Changeset.CurrentPass` (rows since the last Merge →
  Planned) scopes what is not — the fix-round cap (`FixCapOf`), the CI triages a head must report (`CiHeal.Unreported`), and the
  facts in the pull request body, closeout and escalation comment; `merge-files` names the repository merged into
  (`MergeFiles.Repo`, for every plan item's changeset merge — also a plan with one changeset, which may be in another repository
  than the plan item's first), which the freeze's hot-file and main-red read instead of the item's current repository.
  Repository spelling: GitHub's names are case-insensitive and each item carries its own `Repo:` line's spelling, so the merge
  queue (`WorkLedger.ItemsOnRepoAsync`, like `MergeQueue.LockKey`) and the freeze's hot-file key match repositories
  case-insensitively — two spellings never make two queues of one repository. `RepoRef.Key` (lower-case `owner/name`) and
  `RepoRef.Is` are the one comparison for new code (the planner prompt's primary-repository mark, the configured-repositories
  check; sc-25877). Deliberately NOT used for paths on disk: clones and worktrees stay under the literal spelling
  (`<WorkRoot>/repos/<Owner>/<Name>`), because lower-casing them would orphan a running installation's existing directories on
  a case-sensitive volume; the ledger's `Repo` columns and the issue cursors keep the literal spelling too. Metrics (`LedgerMetrics`): a plan item with
  several changesets counts as merged only at its LAST Merge row and only once every changeset is delivered
  (`LedgerMetrics.MergeOf`), and is left out as sandbox work when ANY of its changesets is in a `Metrics:SandboxRepos` repository. Consequence, accepted: while the plan item takes a changeset through the gate it is not Planned, so no
  story is released until it comes back (stories released before go on); an escalated changeset pull request therefore also
  holds the stories not yet released.
  Empty and unused: a repository of the plan item no story uses gets no changeset; an empty changeset (or one a person merged)
  in a several-changeset plan is `changeset-empty` (Detail led by the repository; written once — a run that died before the
  `changeset-delivered` row decides the same again and adds only that row, sc-25877) + `changeset-delivered` and the next repository
  goes on; when the last one needs no pull request the plan closes from Planned (`ClosePlanAsync`).
  Partial delivery: an escalation of a several-changeset plan writes `plan-delivery` (`Changeset.DeliveryOf`: "delivered: a
  (merged into main as …, url); not delivered: b (its changeset pull request url is Escalated), c (… not opened)") after the
  Escalated row and the comment repeats it with the remedies (`Changeset.DeliveryNote`: fix and `factory run`, which goes on with
  the first repository not delivered, or `factory stop --item`); later repositories' changesets are not opened; merged ones stay
  merged. Stop (`ItemStopper`): the open pull request of every changeset is drafted in the changeset's own repository — every
  `Changeset.All(history)` whenever the plan has at least one, never "the item's repository" (a plan whose stories are all in
  another repository than its first has its one changeset there) — `plan-delivery` is recorded before the
  board is told, and the comment says what is delivered. Dashboard: `PlanView.Changesets` (`Changeset.States`, from the plan
  item's rows) shows each repository's changeset; an unreleased story's row shows its own repository. Reports: a changeset pull
  request's body lists its repository's stories and every repository's changeset; the closeout lists each repository's changeset
  pull request and merge commit (`LedgerReport.ChangesetLines`); a changeset review lists that repository's stories, the order
  and the note inside the `plan-stories` fence (`Changeset.ReviewSpec`). Tests: `MultiRepoPlanTests`, `MultiRepoPipelineTests`
  (two `BranchRepo`s behind one router). With PR #69's two checks: `EnsureCiTriggersAsync` runs for every repository of the plan
  item's set before the planner session (above), and `IsChangesetPull` holds per pass (`Run.Changeset` is the pass repository's
  changeset, whose recorded base is that repository's default branch; a story's is null), so each repository's changeset PR is
  sized by its own version 4 `max_changeset_*` and its stories by the ordinary limits (`MultiRepoPipelineTests`).
  Rulesets (`RepoProtection`, unchanged): the `~ALL` ruleset excludes `refs/heads/factory/**`, so the factory App creates and
  pushes `factory/changeset-*` and the gate App merges a PR into it with its ordinary write permission (no rule applies there, so
  no bypass is needed); the changeset PR into the default branch is a PR like any other, merged through the gate App's existing
  `pull_request`-mode bypass. Residuals, accepted: nothing server-side requires a pull request on a changeset branch — whoever
  holds the factory App's token (the orchestrator alone; workers get no git) or an admin can push to it directly — which is why
  the changeset is gated again as a whole before it reaches the default branch; a target repository's CI must run for pull
  requests whose base is `factory/changeset-*` (a workflow limited to `pull_request: branches: [main]` reports nothing, and CI
  then times out and escalates); the board shows a plan's story Done once it merged into the changeset branch, before the plan's
  changeset is on the default branch.
- One run per item: `RunPipeline` holds a Postgres advisory lock on the item id (`PostgresRunLocks`) for the whole
  run, and `WorkItem.Version` is an optimistic concurrency token, so a stale writer's save throws.
- Worker phases (Phase 4b, sc-25988, E6; `WorkerLock.cs`, `RunPipeline.InWorkerPhaseAsync`): a sandbox user is single-tenant
  — every exit of its launch helper kills every process of it — so every run of a helper happens under that sandbox user's
  lock, and a run holds that lock only for its worker phases, not for its whole life (as before sc-25988). This bullet is
  written for the one sandbox user of the default (`Worker:Slots` 1); with several, read "the sandbox user" as "the slot the
  phase holds" throughout — which slot that is, is the next bullet's ("Worker slots").
  The lock: `WorkerLock` = the file `<WorkRoot>/.factory-run.lock` (slot n's: `<WorkRoot>/slot-locks/.factory-run-n.lock`), held exclusively while open (between processes too; the OS
  releases it if the holder dies), waited for — never failed on — and re-entrant per async flow (`HoldAsync`; `IsHeld` is the
  flow's own, and a task that outlives the hold it started in no longer holds it). The sandbox carries it
  (`WorkerSandbox.Lock`, set by `WorkerSlots.Create` for `FactoryOptions.WorkerSlots`, the only place production builds a
  sandbox; unsandboxed there is none). Enforcement, at the one place a helper process starts: `WorkerSandbox.Start` throws unless the calling flow holds the
  lock (nothing is started). Every user of it holds it: the sandbox's own short commands take it themselves (`RunAsync`,
  `EnsureReadyAsync` — the probe and `claude --version` —, `StopAllAsync`, the pgrep included, and `DeleteAsWorkerAsync`, so every
  worktree, seat and sweep deletion of `GitWorkspace`; never under a clone's lock, so no lock-order cycle), as do
  `ClaudeWorker.RunAsync` (a whole session) and `SandboxTestRunner.RunAsync` (a whole run: worktree, steps, deletion) — each at
  once when its caller's phase already holds it. The lock covers the whole life of every helper process, not only the phase
  that started it: whoever starts one ends it with `WorkerSandbox.EndAsync` (the sandbox's short commands, a worker session, a
  gate test step), which closes its stdin, waits for its exit on no cancellation token — up to `WorkerSandbox.StopGrace`
  (10 s) for a short command, so one cancelled mid-way (a Pause or Stop during a test worktree's deletion, a shutdown) does not
  return while its helper is still killing and sweeping — and, if the helper is still alive then, keeps the lock for it
  (`WorkerLock.KeepUntil`): the flow's hold ends and the run goes on (escalates, pauses, stops), but the lock file stays open,
  against every run and process, until that helper has exited. A session whose helper would not stop within the worker's stop
  grace is first stopped the way a crashed run's leftover is — `ClaudeWorker.StopAsync` runs `WorkerSandbox.StopAllAsync` under
  the still-held lock (a second run of the helper, whose exit kills every `_factory` process, the first helper among them) and
  waits once more — and is marked `WorkerStillRunning` only if it is alive even then. A lock kept this way is never silent:
  `[lock] the sandbox user's lock … stays held, N s after its phase ended: a launch helper run as _factory (sudo pid P) … is
  still alive …; kill it if it never exits` on standard error when the phase ends and every `WorkerLock.ReportEvery` (30 s)
  after; runs waiting on it wait as on any holder, honouring Pause and Stop (below). The phases a run holds it for (`InWorkerPhaseAsync`), so that nothing else runs
  as the sandbox user between a session and its push: Implement, from the orphan's stop to the worktree's removal (the worktree,
  the session, commit and push, the PR's opening); a fix round (review, CI, conflict) likewise; a plan item's planner seat
  (orphan stop, sweeps, checkouts, session, removal; `SandboxPlanRunner` takes it too); the gate's new-tests runs
  (`RunNewTestsAsync`); the stop of a leftover worker in `StopIdleAsync`; and, outside the pipeline, a whole triage
  (`SandboxTriageRunner`: probe, sweeps, session, removal). Not held: Intake, the review panel's calls, CI and CodeGraph waits,
  the merge queue and the merge, a waiting plan's evaluation, board and GitHub writes outside those phases. Waiting: a run that
  finds the lock taken waits at that phase (log `[lock] … waiting for the sandbox user to …`), reading its item's controls every
  control poll — a Pause (the usage pause and the freeze's row included) or a Stop ends the wait exactly as it ends a step
  (Paused `user-paused`/`usage-paused`/`freeze-paused`, or the stop), controls unreadable more than `Controls:MaxReadFailures`
  times in a row pause it (`controls-unreadable`, E2) — and once it has the lock the controls and the freeze evaluator are asked
  again before the phase starts. So `factory run` beside `factory work` no longer fails at start with "another factory run": it
  runs, and waits at a worker phase. No wait for the lock is silent or endless by accident (`WorkerLock.HoldAsync`): beside the
  line a run logs when its wait starts, every wait — a run's, a triage's, a stop's, a sandbox command's own — is reported again
  every `WorkerLock.ReportEvery` (30 s) with why the last try failed (`[lock] waited N s for the sandbox user's lock … and still
  waiting …: <error>`; a run's and a triage's to their log, the others to standard error), so an I/O error that does not go
  away, which .NET cannot tell from "held" on an existing file and which is therefore retried, shows; and a lock file that
  cannot be opened for any other reason (a work root that cannot be made, no access) is a `FactoryUnavailableException`
  (`the sandbox user's lock: … cannot be opened`), thrown by the lock itself wherever it is taken: the item, already claimed and
  at its worker phase, is left where it is and nothing is recorded against it (E10). The two waits outside a run: a triage's
  (`SandboxTriageRunner`) logs `[lock] gh-N: its triage is waiting for the sandbox user …`, ends with the host, and reads its
  item's controls every `RunPipeline.DefaultControlPollInterval` — once one holds the item (a Pause of the factory or the item,
  the usage pause, the freeze, a Stop: what the intake checks before it starts a triage) the triage is put off with
  `TriageDeferredException`, which `IssueIntake` treats as "not this poll" (logged; no failure counted, nothing recorded,
  the issue read again next poll); a stop's wait for the orphan's kill (`StopIdleAsync`) logs `[lock] a stop: waiting …`, ends
  on cancellation and is deliberately not control-aware (a stop is not held up by a control: there is nothing a Pause could
  mean for it). The run's first worker phase starts, under the lock, with the set-up every run
  shares (`FactoryRunner`'s `enterSandbox`): `EnsureReadyAsync`, then the sweep of worktrees no run will resume — a failure there
  is the factory's (`FactoryUnavailableException`: nothing recorded against the item); a run with no worker phase does neither.
  Clean-up outside a phase (an escalation's or a stop's worktree, the merge queue's base-update worktree:
  `RunPipeline.RemoveWorktreeAsync`) deletes only if the lock is free now (`TryHoldAsync`) — a run does not hold its item, or its
  repository's queue turn, for another run's whole session to throw a directory away; left behind (`[cleanup] the sandbox user is
  busy …`), it is deleted by the sweep the next worker phase of any run starts with, or replaced by the item's next worktree.
  The rule for every path that can reach a deletion as the worker user (`GitWorkspace` → `IWorkerSandbox.DeleteAsWorkerAsync`)
  outside a worker phase: it is either a control-aware phase wait or a try that leaves the directory. The paths: (1) the merge
  queue's base update (`UpdateFromBaseAsync`) restores the item's own `factory-<id>` worktree — with nothing there
  (`IRepoWorkspace.HasLeftoverAsync` false: the directory does not exist) a fresh worktree runs no helper, so the restore takes
  no lock; with a leftover there (an earlier pass's clean-up found the sandbox user busy) replacing it deletes it as the worker
  user first, so the restore is a worker phase (`[lock] sc-N: waiting for the sandbox user to replace its leftover worktree`,
  ended by a Pause, a Stop or the freeze like any); (2) that update's, an escalation's and a stop's removal: `TryHoldAsync`,
  above; (3) the clean-up of a worktree whose creation failed under the clone's lock (`GitWorkspace.ReplaceWorktreeAsync`, any
  caller, on no cancellation token) never waits for the worker user (`IWorkerSandbox.TryDeleteAsWorkerAsync`: inside a phase it
  has the lock; outside, busy, it leaves the directory — a leftover for (1) or the sweep). Everything else that deletes as the
  worker user is inside a hold: Implement's and a fix round's Prepare/Restore/Reopen/Remove, the planner's seat (its
  `PrepareSeatAsync`/`RemoveSeatAsync` and their failure clean-up), a triage's worktree, the gate's test worktrees
  (`PrepareCommitAsync`, removal), and the three sweeps (a run's at its first worker phase, the triage's, the planner's).
  Dispatch (`IntakeLoop`): a poll renews every lane's claims, then lists every lane and starts its items — each lane's in-flight
  ones (stops first), then its ready ones, each once — into the capacity `Intake:MaxConcurrentRuns` leaves beside the runs still
  in flight, and does not wait for them (sc-25992, "Runs across polls"); stops and waiting plans start outside that capacity;
  with 1 (the default) the other runs are one after another as they were. Claims are renewed once per poll and lane, also while runs are in flight (`RenewClaimsAsync`, sc-25983), never per
  dispatched run. Still one run per item (the item's run lock) and one turn per repository (the merge queue).
  What stops a poll's new work still does with several runs in flight: after a run the freeze deferred no ready item of any
  lane and no further in-flight item of its lane is started; after a factory-wide failure nothing is; a factory-wide control
  set since the poll listed its work (the usage pause, a Pause of the factory, the freeze's row) is read before each ready item
  and starts none (sc-25991; the rules, and what the status and the page show of several runs, are under "Intake failures" —
  "Runs in flight" — and "Dashboard"); runs already started
  finish by themselves, each checking the freeze, the usage pause and its controls before every step. Residuals, accepted: (a) one
  sandbox user means one worker phase at a time — with `Worker:Slots` 1, or for the items of one slot — so a run whose turn in
  a repository's merge queue needs the new-tests runs, or
  finds a leftover of its own worktree to replace, waits for another run's session in its slot and holds that turn meanwhile
  (both waits honour Pause, Stop and the freeze); (b) a helper that never exits (wedged in sudo, say, where the second helper run cannot
  reach it) keeps the lock, and so every worker phase of every run, until someone kills it — by design: its exit would kill
  whatever ran as `_factory` then; the periodic `[lock] … stays held … (sudo pid P)` line names it, and waiting runs can be
  paused or stopped; (c) a worktree is shared by ACL with one sandbox user — its item's slot's — so a worker can
  write another item's kept worktree of the same slot, as it could between runs before, and never one of another slot
  (checked before every push of worker work: "Slot isolation", sc-25990); (d) the
  acceptance harness (`E2e`) still takes the configured work root's locks — every slot's — for its whole life
  (`WorkerLock.Acquire`, the one fail-fast taker), so a real `factory work` beside it waits at its worker phases rather than failing; (e) the CI fixer's
  prompt reads the failing checks from GitHub once inside its phase (one bounded read under the lock, not CI's polling).
  Tests: `WorkerLockTests` (also: a kept lock, its reports, a wait's report, a lock that cannot be opened);
  `LockScopeTests` (a probe on every call the pipeline makes of its worker, workspaces, test runner, reviewers and GitHub: the
  sandbox calls all hold the lock, the review, CI and merge calls never do; two runs overlapping; waits honouring Pause, Stop
  and the freeze; a worker that would not stop keeping every other run's session out until its helper is gone; a base update,
  a conflict fix round and a CI fix round through the simulated repository of `MergeQueueTests` — the one restore allowed
  without the lock is the base update's with no leftover, and over a leftover it waits, logged, honouring Pause, Stop and the
  freeze; the source scan that production builds a sandbox in one place and starts the helper only through `Start`);
  `WorkerSandboxTests`/`SandboxReadinessTests` (a sudo stand-in that asks the OS, from its own process, whether the lock file is
  locked at every helper run: `SandboxSupport.LockProbingSudo`; a wedged "helper" stopped by the second helper run, one that
  will not exit keeping the lock until it does, a cancelled command waited for while it sweeps; the start-up check reading the
  helper with the lock held elsewhere); `GitWorkspaceTests` (the failed worktree's clean-up never waiting); `IntakeLoopTests`
  (a run's stale-helper refusal before the ledger; a triage's wait); `PlanTests` (the planner's seat); `IntakeDispatchTests`.
- Worker slots (Phase 4b, sc-25989, E6/E7; `Worker/WorkerSlots.cs`, `RunPipeline.InWorkerPhaseAsync`): `Worker:Slots`
  (1–8, default 1) sandbox users run workers side by side. A slot = a sandbox user + its own installed launch helper + its own
  lock file (`WorkerSlot`: a number and a `WorkerSandbox`): slot 1 is `Worker:RunAs` through `Worker:LaunchHelper` under
  `<WorkRoot>/.factory-run.lock` — exactly the sandbox before slots — and slot n is `<Worker:RunAs>n` through
  `<Worker:LaunchHelper>-n` under `<WorkRoot>/slot-locks/.factory-run-n.lock` (`WorkerSlots.UserOf`/`HelperOf`,
  `WorkerLock.For(root, n)`: the names `setup-worker-user.sh --slots n` creates and installs). The lock files of the slots
  above 1 are in a directory of their own (`WorkerLock.SlotDirectoryName`) that the pool closes to every sandbox user before
  it takes any lock (`WorkerSlots.EnsureLocksClosedAsync`, from `HoldAsync`, `TryHoldForAsync` and `AssignedAsync` — every way
  the pool takes or hands out a slot, a waiting deletion's included; once per pool): the work root's inherited read entry would let one slot's user read another slot's lock
  file, and `flock` takes an exclusive lock through a read-only descriptor, so code a worker runs could hold another slot's
  lock and make that slot look busy. Closed as `GitWorkspace` closes `clone-locks`: owner-side, no root
  (`WorkerSandbox.CloseToWorkerAsync`: `chmod -N`, mode 0700, read back with `ls -led`; off macOS, where no sandbox user can
  be set up, mode 0700 read back), and a directory that cannot be closed is the factory's failure
  (`FactoryUnavailableException`: `the worker slots' locks: …`; no slot's lock is taken, slot 1's included). A pool refuses
  (at construction) a slot above 1 whose lock is beside slot 1's. Nothing of this with one slot or `Worker:RunAs=none`. Each helper's sweep is of its own uid only, so two runs are in a
  worker phase at once exactly when they hold different slots, and nothing one slot's helper does reaches another slot's
  processes. The pool (`WorkerSlots.HoldAsync`) hands out a slot, waiting like the single lock did (control-aware through the
  pipeline's `whileWaiting`, reported every `WorkerLock.ReportEvery`: `[lock] waited N s for a free worker slot …`): a named
  slot — only that one, however long it is busy — or the first free one in a given order. A flow inside a slot runs any nested
  phase in it and is refused another (a phase is one sandbox user's). Everything done as a sandbox user is done by the slot the
  calling flow holds (`WorkerSlots.Current`): the pool is the `IWorkerSandbox` of every `GitWorkspace` and the sandbox of
  `ClaudeWorker` and `SandboxTestRunner`, resolved per call; with several slots a flow that holds none has no sandbox user and
  `Current` throws, and `WorkerSandbox.Start` still refuses without that slot's lock.
  An item's slot (E7): its first worker phase takes a free slot — tried in the order of fewest unfinished items first
  (`WorkLedger.SlotsByLoadAsync`: items not Done, Cancelled or in Watch, by their last `slot` row; ties by number), so slots
  fill evenly rather than in number order — and records it as a `slot` checkpoint (Detail `2 _factory2`; outcome passed) under
  that slot's lock, before the run's sandbox set-up or the phase creates anything. From then on EVERY worker phase of the item
  waits for that slot and no other, for the item's whole life (`RunPipeline.SlotOf`, read once per run): a resumed or paused
  session (its transcript is in that user's home, so `claude --resume` works nowhere else), a fix round (review, CI, conflict)
  and a changeset's fix round, the gate's new-tests runs, a plan item's planner seat, the stop of a worker a crashed run left
  (`ClaudeWorker.StopOrphanAsync` → that slot's `StopAllAsync`: only that slot's helper runs, so only that user's processes are
  killed; `StopIdleAsync` waits for the slot, logged `[lock] a stop: waiting for worker slot 2 (_factory2) …`), and its
  worktrees' deletion. The wait is the worker phase's (`[lock] sc-N: waiting for worker slot 2 (_factory2) (another run's
  worker phase holds …) to implement`, ended by a Pause, a Stop or the freeze). An item with a `worker-started` row and no
  `slot` row was worked before slots, or by a one-slot factory (which writes no `slot` row: its ledger is as before, E9): it is
  slot 1's. An item whose recorded slot is above `Worker:Slots` (the setting was lowered) fails with the reason — `sc-N was
  started in worker slot 3 (_factory3), but Worker:Slots is 2 …` (`SlotUnavailableException`; Escalated) — and nothing of it
  is run or deleted as another user. The reason names the two remedies and what each does. (1) `Set Worker:Slots to at least
  3 again (with that user set up: sudo scripts/setup-worker-user.sh --slots 3) and re-run it`: the re-run (Escalated → Intake)
  goes on in slot 3 — its session, its worktree, its `slot` row unchanged. A re-run WITHOUT raising the setting fails again
  with the same reason at its first worker phase: no session, no helper, no worktree made, removed or reopened, no new `slot`
  row — an item is never restarted in another slot (its branch and worktree path are the same, and the old worktree is another
  user's). (2) `Or stop it (factory stop --item sc-N)`: the stop reaches Cancelled like any stop (`StopIdleAsync` →
  `SlotLeftAsync`), but runs nothing as another user and deletes nothing: a worker a crashed run left is NOT stopped (no
  helper of a configured slot could reach that user's processes), and its worktree is neither reopened nor removed. What stays
  is on the record before the board is told — a `slot-left` checkpoint (`RunPipeline.SlotLeftDetail`: the slot and user, the
  pid of the worker left running if there is one, and how to get rid of both; typed outcome `failed`; written once) — and the
  stop's comment ends with that row's text (`ItemStopper.StopItemAsync`, from the ledger). The plan-stop cascade
  (`ItemStopper.StopStoriesAsync`) and `GiveUpAsync` start no worker phase and read no slot, so neither can meet the
  exception: a story of a stopped plan whose crashed worker is on record is left to its own next run, which stops it this way.
  Which slot the work with no item of its own uses: a triage — any free slot, in number order, recorded nowhere (nothing
  resumes a triage and its worktree is shared with no one for writing; `SandboxTriageRunner`); a run's sandbox set-up
  (`enterSandbox`, in its first worker phase) — the slot that phase holds: the readiness probe is of that slot's helper, and
  the sweep runs there; the merge queue's base update, which makes a fresh worktree outside any worker phase and runs no helper
  — no slot is held, and the worktree is shared with the item's slot's user by the owner's own `chmod`
  (`RunPipeline.InItemSlotAsync` → `WorkerSlots.AssignedAsync`; an item with no slot yet, e.g. a plan item that never needed a
  planner here, is given the least-loaded one, recorded then).
  Deletions: a directory is deleted as the user it was shared with for writing, under that slot's lock — read from the
  directory's own ACL (`WorkerSlots.SharedWith`, the `ls -led` listing; the directory is the owner's, so only
  `WorkerSandbox.ShareAsync` wrote the entry, and for an item's worktree it is the item's recorded slot), never chosen another
  way. A flow that holds no slot waits for it (`DeleteAsWorkerAsync`), or tries it (`TryDeleteAsWorkerAsync`,
  `WorkerSlots.TryHoldForAsync`: the pipeline's clean-up outside a phase); a flow that holds another slot — a sweep in another
  item's worker phase — never waits for a second slot (two such runs would wait on each other): every sweep deletes with the
  try (`GitWorkspace.SweepOrphansAsync`), so a leftover of a busy slot is left to a sweep that finds it free. A directory shared
  with no slot (a triage's or a planner's read-only worktree, a seat) was written by no worker: the held slot deletes it, or
  any free one. Fail closed: a directory writable by a principal that is no slot's user — a slot above `Worker:Slots`, two
  slots' users, a group, an entry `ls` printed as a UUID (it does that while the directory service does not answer: the listing
  is read up to 3 times first) — is not deleted as anyone: not as another slot's user, and never owner-side (the owner would
  walk a tree that user wrote). `WorkerSlots.SharedWith` throws the typed `ForeignWriterException`. A deletion that must
  happen (`DeleteAsWorkerAsync`: an item replacing that very worktree) fails with it, naming the directory. Every deletion
  that may be left — `TryDeleteAsWorkerAsync`/`TryHoldForAsync`, so every sweep (`GitWorkspace.SweepOrphansAsync` for the
  items' worktrees, the gate's test worktrees, the triage worktrees and the planner seats alike: one path) and the pipeline's
  clean-up outside a phase — leaves the directory, runs no helper for it, and goes on to the next: a leftover of a removed
  slot never fails a sweep, which would be the whole factory's failure in every run's first worker phase. It is said once per
  directory per process, on standard error (and in the run's log when its own clean-up meets it): `[cleanup] leaving <path>:
  it is shared for writing with user:_factory3, not with one of this factory's 2 sandbox users (…). … To get rid of it: set
  Worker:Slots back so that user is one of this factory's (the next worktree sweep then deletes it as that user), or remove it
  by hand as root (sudo rm -rf -- '<path>')` (`WorkerSlots.LeftReport`, `LeftForeign`). MCP session files are the owner's own
  files and are deleted owner-side; no sweep of them deletes as a worker. With ONE slot no ACL decides which user deletes
  (everything is slot 1's, as before slots, E9), with one addition for a factory lowered to 1: a directory writable by a
  higher slot's user of this factory's naming (`<Worker:RunAs>2` … `8`) is left, or refused, the same way
  (`ThrowIfAHigherSlotsAsync`) — slot 1's user could not delete it and the owner would have; any other entry, and a listing
  that cannot be read, change nothing there.
  `Worker:RunAs=none` (development): no sandbox user, no lock, no slot — `Worker:Slots` other than 1 is refused at start-up
  (`FactoryOptions.WorkerSlotCount`) rather than ignored. With `Worker:Slots` unset nothing changed: one user `_factory`, one
  helper at the same installed path, one lock file, no `slot` row, the same log lines (`… waiting for the sandbox user …`).
  Not this story (W4): per-slot MCP session directories and seat roots (both still shared under the work root; the session
  file's ACL check already names the slot's user), the cross-slot write-denial checks, and the documentation of the cross-slot
  read residual. Residuals, accepted: (a) the slot is the item's for life, so an item waits for its own slot while another is
  free — the price of never running or deleting anything of an item as a user that did not write it; the first-phase order by
  load keeps the slots even; (b) sandbox users can read each other's worktrees through the work root's inherited read entry
  (E7: accepted, written up with W4's checks); (c) lowering `Worker:Slots` with items still in a higher slot escalates those
  items when they next need a worker (raise it again and re-run them, or stop them), and whatever that slot's user left — their
  worktrees, a worker a crashed run left running — stays on the machine until the setting is raised again or the owner removes
  it as root: the factory says so (the `[cleanup] leaving …` line, the `slot-left` row and stop comment) and touches none of it; (d) a
  directory's slot is read from an ACL listing, so a wrong answer from `ls` is a loud failed deletion, not a deletion as the
  wrong user; (e) slot 1's own lock file stays `<WorkRoot>/.factory-run.lock` (E9: the path it had before slots), in the work
  root, which every sandbox user reads: code any slot's worker runs can hold slot 1's lock and make slot 1 look busy — liveness
  only (no run is started, stopped or deleted by it; waits are reported every 30 s and honour Pause and Stop), and as it was
  before slots for the one sandbox user's own lock.
  Tests: `SlotPoolTests` (the pool: names, the refusals of a shared user/helper/lock, two flows in two slots with a third
  waiting, a named slot waited for beside a free one, the order, `Current` and `AssignedAsync`; which slot a directory is
  deleted in, through fake helpers that record which ran; the share on macOS; start-up naming the slot whose helper is missing,
  stale, edited, pinned to another user or unpinned, and the probe of the held slot only; the pipeline: two items in a session
  at once as two users, the `slot` row written before the set-up, an item interrupted in slot 2 resuming its session there
  while slot 1 is free, a leftover worker stopped through slot 2 only, a slot no longer configured — its failure's two remedies, a re-run with and
  without the setting raised, and its stop with and without a worker on record —, a sweep leaving a foreign leftover once and
  deleting the next, the one-slot rule for a higher slot's leftover, the lock directory closed before any lock by each of the
  five entry points and once, a directory that cannot be closed, a one-slot factory
  recording nothing and a pre-slots item being slot 1's, the order by load), `LockScopeTests` (the base update's worktree is
  the item's slot's user's with no slot held; production builds sandboxes only in the pool), `WorkerSandboxTests` (through
  `SafeHelper` only: each slot's helper copy sweeps its own uid's processes and never the other's, a copy run as another slot's
  user sweeps nothing, one pinned to another slot's uid refuses; version 4; the watcher's one redirected group and 200 runs with
  an empty stderr), `SetupScriptTests` (`--slots 2`: two users, uids, homes, ACLs, per-user helper pins the orchestrator accepts
  for that user only, one sudoers line each, the toolchain checked through each user's own helper; a shared uid and a count
  outside 1–8 refused; a re-run with fewer slots removing exactly the higher slots' helper paths, after the sudoers file, and
  naming the users left; `--slots 8` removing none; `--remove-worker-login` per user), `FactoryOptionsTests` (`Worker:Slots` read, bounded, refused with
  `Worker:RunAs=none`). Live, not run here: P4b-AT5 and P4b-AT6 (`SlotAcceptanceTests`; docs/acceptance.md).
- Slot isolation (Phase 4b, sc-25990, E7/E4; `Worker/WorkerSlots.cs`, `Worker/WorkerSandbox.cs`, `Mcp/McpSessions.cs`,
  `FactoryRunner.cs`): what used to rest on "no other sandbox process runs" holds between slots. Owner decision 2026-10-13:
  slots are isolated for WRITING, and each has its own MCP session directory; cross-slot READS are accepted (below). Everything
  here is owner-side — the owner's own `chmod` and `/bin/ls -led` on directories the owner owns, and the owner's own read of
  modes and ACLs inside a worktree; no root, no change to `setup-worker-user.sh` (the launch helper's change in this story is
  its stderr only: "The helper's stderr", under the tests).
  Layout (`WorkerSlots.DirectoryOf(name, slot)`: `name` for slot 1, `name-n` for slot n, as the helpers are named): slot 1 keeps
  `<WorkRoot>/mcp-sessions`, `triage-worktrees` and `plan-worktrees` exactly where they were (E9: with `Worker:Slots` 1 no
  path and no behaviour changed, and what an earlier version left there is still swept); slot n has `mcp-sessions-n`,
  `triage-worktrees-n`, `plan-worktrees-n` (`McpProxySessions.DirectoryOf`, `SandboxTriageRunner.TriageWorktreesOf`,
  `SandboxPlanRunner.PlanWorktreesOf`). The items' `worktrees`, the clones (`repos`), `clone-locks`, `slot-locks` and `controls`
  are shared by all slots as before.
  (1) Worktrees, write isolation: a worktree is shared for writing with one sandbox user — its item's slot's — by the
  inheritable ACL the owner sets on the empty directory (`WorkerSandbox.ShareAsync`, sc-25989), so another slot's user has no
  write entry on it. Since a push grant vouches only for the item's own sessions (E4: a tainted session must not change files
  an untainted session later pushes), `GitWorkspace.CommitAndPushAsync` — every push of worker work — first asks, before
  anything is staged, committed or a token minted, `IWorkerSandbox.EnsureOnlyWorkerWritesAsync(worktree)`: the worktree's `ls
  -led` must name no writer but the owner and the user of the slot the pushing phase holds (`WorkerSlots.Current`;
  `WorkerSlots.WritersBeside`: any `allow` entry with a write right — `write`, `append`, `add_file`, `add_subdirectory`,
  `delete`, `delete_child` — for another slot's user, any other user, a group or a UUID `ls` could not name, or a mode that
  lets everyone write; any later line of the listing that is neither an `allow` nor a `deny` entry — a principal with a space
  in its name, a format this does not know — is itself a reason, never skipped). Otherwise `WorktreeWritersException`
  ("Refusing to push the work in <path>: its ACL lets user:_factory write it. Only _factory2 … and the owner may write an
  item's worktree and what is in it …"): nothing is staged, committed or pushed and no token is minted.
  The root is not enough with several slots: its ACL only its owner (the factory's owner) or root can change, but the files
  and directories a worker makes inside are that sandbox user's OWN, and code it runs (a build script's `chmod -R a+w`, a tool
  with umask 0, a `chmod +a`) can open them to another slot's user without touching the root — the work root lets every
  slot's user search and read down to them. So with `Worker:Slots` > 1 the same call then walks the whole worktree owner-side
  (`Worker/WorktreeWriters.cs`, `WorktreeWriters.Inside`; in process, no `ls`): every entry, hidden and git-ignored ones
  included (a build input another slot could have planted is as much a way in as a tracked file; the worktree's `.git` is a
  file and is judged like any; the admin directory is the clone's, outside the worktree), never following a symbolic link (a
  link is neither judged nor entered: changing one takes write on its directory, which is judged). It refuses the first entry
  (a) whose mode has the other-write bit; (b) whose mode has the group-write bit — refused outright, also on the root with
  several slots: setup gives each sandbox user a primary group of its own that no other slot's user is in and a file made here
  takes its directory's group (the owner's), so the bit opens nothing between slots in practice, but proving that would take
  a membership lookup per group and nothing made under the usual umask 022 has it; (c) whose ACL — read from the kernel for
  that entry itself (`acl_get_link_np`, printed by `acl_to_text`, once per distinct ACL: `WorktreeWriters.NativeAcl`) — has
  an `allow` entry for anyone but the owner and the worktree's own slot's user with any right beyond `read`, `execute`,
  `readattr`, `readextattr`, `readsecurity` (so `write`, `append`, `delete`, `delete_child`, `writeattr`, `writeextattr`,
  `writesecurity`, `chown` and any right this does not know; the other slots' inherited read entries pass: the accepted
  cross-slot read); or (d) that cannot be judged: a directory the owner cannot list, an entry that vanished or whose name
  does not read back, an ACL that cannot be read or has a line that is no entry or names no one. The reason names the entry
  relative to the worktree ("src/deep/work.txt in it has a mode (0646) that lets every user write it", "the ACL of … lets
  user:_factory write it (write)"). Nothing is skipped and there is no size bound: the cost is linear in the entries, like
  `git add -A` itself — measured on this repository's worktree 1,137 entries in about 10 ms, and 5,050 entries each holding a
  four-entry ACL in about 60 ms on a loaded host (about 12 µs an entry; roughly a second per 100,000) — and the walk is
  cancellable. With ONE slot nothing inside is read and the root's group bit is not judged (E9: there is no other slot's user
  to open a file to; the root's listing is read as before, so a worktree a higher slot's user may write, left from a larger
  `Worker:Slots`, is not pushed from).
  A refusal escalates the item with that reason (typed outcome `escalated`, like a tainted session's refusal) and, like any
  escalation, its worktree is removed: deliberately not kept — what is in it cannot be vouched for, so it is neither resumed
  nor pushed later — and said in the reason (so in the escalation comment): the session's work is discarded, a re-run does it
  again in a fresh worktree, and if it happens again something the worker runs in that repository opens files to other users
  (`chmod go-w`/umask 022 there, or `Worker:Slots` 1). Because a refusal costs a finished session, it is not decided in a
  hurry: who may write is read up to 10 times, 200 ms apart (`WorkerSlots.PushAclReads`, about 2 s; with one slot too) —
  `ls` prints a user's UUID, and an ACL entry has no name, while the directory service is slow to answer. Fail closed: a
  listing that cannot be read, or shows no directory, refuses at once. Not checked: a seat's read-only worktree
  (`shareWithWorker` false — never pushed) and the merge queue's base-update push (`PushAsync`: the orchestrator's own merge
  commit, no worker work).
  Residuals of (1), accepted: the check is of the worktree as it is at the push, when no process of its slot's user is left
  (the helper's exit kills them all), so a file that was open to another slot's user WHILE the session ran and was closed
  again before the push is not seen; and it is the factory's own reading of modes and ACLs, not the kernel's answer to "may
  that user write this" (an access path that is neither a mode bit nor an ACL entry — there is none known for a role account
  with a group of its own — would not be seen). Ownership is not read either: an entry in the worktree that another slot's
  user OWNS (possible only if the worktree's own worker hard-links that user's file into its tree; another slot's user cannot
  create anything there) could be rewritten by its owner under ordinary modes — code that does that could as well copy what
  the other slot left in a shared place (the `/tmp` residual below). An owner whose umask gives group write (002) has every
  push refused with several slots: run the factory under 022, macOS's default.
  (2) MCP session files, read isolation: the work root's inherited read entry names every slot's user, so with several slots
  each session directory is made its own slot's user's alone before a session's file is written
  (`WorkerSandbox.RestrictToWorkerAsync`, every slot's, slot 1's `mcp-sessions` included: `chmod -N` — every entry gone, the
  inherited ones too, and nothing made there later inherits one —, mode 0700, then two entries for that user: `search` on the
  directory itself, so it opens the file it is given and cannot list the directory, and an inherit-only `read` for the files
  made in it; read back with `ls -led`). Once the file is written `McpProxySessions` reads both back: the slot's user must
  hold `search` on the directory and `read` on the file (as before), and neither may be open to anyone else but the owner
  (`WorkerSandbox.OpenToOthers`: no `allow` entry of another principal for any right, no group or other mode bit, no line that
  is not an ACL entry; read up to 5 times before it fails). Any of it failing — the restriction, a read of the listing, the
  check — is factory-wide as before (`FactoryUnavailableException`, `the MCP session config: … must be readable by _factory2
  (its worker slot's user) and the owner alone, but its ACL also allows user:_factory, so another slot's worker could read
  this session's MCP credential`): the session is not started, its file is deleted and its credential revoked. With one slot
  nothing is restricted and only the old check runs (E9).
  (3) Seats: a triage's and a planner's worktree root and session directory are those of the slot the seat runs in
  (`SlotSeat.Of(workRoot, name, slots, slot)` in `FactoryRunner.cs`, the one place both production runners get a seat's
  worktree root, session directory, user and restriction from) (a triage:
  whichever free slot it took; a planner: its plan item's slot, so a re-run finds what an earlier run of it left), and each
  seat's two sweeps (`GitWorkspace.SweepOrphansAsync` of its seat root, `McpProxySessions.SweepAsync` of its session directory,
  both under the `LiveRuns` claims of sc-25987, unchanged) are of those two directories only: a triage in one slot and a
  planner in another, at once, neither sweep nor use each other's. One exception, narrow: a triage takes any free slot, so an
  earlier, dead triage of the SAME issue may have left its worktree in another slot's root, and the clone refuses a second
  worktree of `factory/triage-gh-<key>` while it is there — so a triage first removes its own issue's leftover, and nothing
  else, from the other slots' triage roots (`SandboxTriageRunner.RemoveOwnLeftoversAsync`: it holds that item's run lock, and
  a seat's worktree is shared with no worker for writing, so no slot's user wrote it). A dead session's file in another slot's
  directory holds a credential that died with its process's proxy and waits for that slot's next seat.
  Residual, accepted (owner decision 2026-10-13): cross-slot READ. The work root's inherited read entry
  (`setup-worker-user.sh`) gives every slot's user read on everything made under the work root that is not closed owner-side
  (`clone-locks`, `slot-locks`, and with several slots each `mcp-sessions[-n]`): the clones, every item's worktree and every
  seat's worktree, of every slot. So code a worker of one slot runs can read another slot's worktree — another item's
  unmerged work, of any repository the factory works in — while both run; it cannot change it, and what it read reaches a
  push only through its own session, which a web or MCP use taints (E4) and review and the gate judge. For triage this means
  its read confinement is, between slots as within one, Claude Code's own: the triage session's only allow rule is the
  `ReadRule` of its own worktree with `dontAsk` and `blockReadsOutsideWorkingDirectories`, and the OS does not back it —
  the triage's sandbox user can read every other slot's worktrees (and, as before slots, its own slot's kept worktrees and
  the clones). A gap in that enforcement (the Glob/Grep residual under the triage bullet) would therefore reach other slots'
  unmerged work too, inside the triage's bounded, fenced answer. Also accepted: a sandbox user can still write its OWN slot's
  other kept worktrees (Worker phases' residual (c)), and (2)'s restriction is re-applied by each session rather than once,
  so a session directory someone opened up by hand is closed again by the next session, not before it. Also accepted:
  world-writable places OUTSIDE the work root are shared by every slot's user — `/tmp`, `/private/tmp`, `/var/tmp`, the
  scratch files NuGet, MSBuild, cargo and other tools put there — so code one slot runs can plant or change a file there that
  another slot's build reads (a slot's own `$TMPDIR` is per user, but nothing makes a tool use it). Nothing checks those: what
  such a build then produces still goes through review, CI and the gate, and a web or MCP use taints the session that planted
  it only for its own push.
  Tests: `SlotIsolationTests` (the write-grant and the read-alone decisions over `ls -led` listings; the pool asking about the
  held slot; the ACL of an entry inside a worktree and the walk of a real tree — links, hidden files, an unreadable or
  vanished directory; on macOS, with the role accounts `daemon` and `nobody` for two slots' users, a real restricted
  directory and a real worktree whose push is refused once the other user may write it or a file inside it, by a real ACL
  entry or a mode bit; `SlotSeat.Of`, and that the two production runners take every seat directory from it),
  `GitWorkspaceTests` (the refused push, for the root and for an entry inside: nothing staged, committed or pushed, no
  token; ordinary modes and a link out of the worktree still push; one slot reads nothing inside), `McpProxyTests` (the
  restricted directory, each refusal, one slot unchanged), `SafeCleanupTests` (the layout; a triage and a planner in two
  slots at once, each with the directories `SlotSeat.Of` gives its slot; the triage's own leftover in another slot's root).
- Clean-up under overlapping runs (Phase 4b, sc-25987; `Ledger/LiveRuns.cs`, `Git/CloneLock.cs`): no sweep decides from "no other run exists".
  Whose a leftover is: it carries its item in its name — a worktree directory by `ItemNaming.OwnerOfLeftover` (`factory-<item>`,
  the changeset worktree `factory-changeset-<item>`, a triage's `factory-triage-<item>`, a planner's worktree or seat
  `factory-plan-<item>`, the gate's test worktrees `gate-<item>[-base|-head|-base-retry[-N]]`; the merge queue's base-update
  worktree is the item's own `factory-<item>`), an MCP session file by `ItemNaming.OwnerOfSessionFile` (`<item>.<guid>.json`:
  `McpProxySessions` is given its `owner`, the item whose run opens the sessions) — and it is a live run's exactly while that
  item's run lock is held. Each sweep asks `LiveRuns.TryClaimAsync(owner)`: null (another run holds the item's lock) → nothing
  of it is touched; otherwise a `LeftoverClaim`, for another item that item's run lock itself, held until the deletion (the
  directory and the clone's `worktree prune`) is done, so no run of it starts in between. A name of no item, an item the ledger
  never saw (nothing can run it; so also a session file written before files were named) and the asking session's own item
  (`self`: its run already holds that lock, and what an earlier, dead run of it left is its to delete) are claimed without a
  lock. The three sweeps: the start-of-run worktree sweep (`FactoryRunner.RunAsync` → `GitWorkspace.SweepOrphansAsync(keep,
  live, ct)`: the ledger's rule `RunPipeline.WorktreeIsResumableAsync` is asked first and a kept worktree takes no lock; one it
  would delete is claimed, the rule asked again under the lock — the item's run may have ended as the lock was taken — and only
  then deleted; no `self`, since the pipeline has not taken its item's lock yet), the triage's and the planner's seat sweeps
  (`SandboxTriageRunner`, `SandboxPlanRunner`: nothing is resumed, `self` = the session's item, `SandboxTriageRunner.Leftovers`)
  and the MCP session-file sweep (`McpProxySessions.SweepAsync(directory, live, ct)`, by both). A run's worktree sweep now runs
  at its first worker phase, under the sandbox user's lock (sc-25988), and its own item's leftovers are then a live run's (it
  holds that run lock): it replaces them itself when it makes its next worktree. With no other run every other lock is free. A swept item's run lock is held only for the
  deletion, so a `factory stop` of that item landing in that moment answers "is running" and is finished by the next poll.
  The shared clone: `<WorkRoot>/repos/<owner>/<name>` has one writer at a time, `CloneLock` — a lock file
  `<WorkRoot>/clone-locks/<owner>/<name>.lock` held exclusively while open (between processes too; the OS frees it if its holder
  dies), waited for with a 50 ms retry until the caller's token is cancelled. A wait is never silent (clean-up waits with no
  cancellation): once it has lasted `CloneLock.ReportEvery` (30 s), and every 30 s after, a line goes to standard error —
  `[clone-lock] waited N s for <lock file> and still waiting (…): <why the last try failed>` (`CloneLock.WaitReport`) — so a
  stuck holder, or an I/O error on the lock file that does not go away (.NET reports both alike, so both are retried), shows.
  `GitWorkspace` holds it around every git
  operation that changes the clone's refs or worktree list, together with the ref reads that depend on what it just wrote:
  clone/fetch/`remote set-head` through the start-point read, the prune of the replaced worktree's registration and `worktree
  add` in one hold (`ReplaceWorktreeAsync`: `PrepareAsync`, `RestoreAsync`, each checkout of `PrepareSeatAsync`, and
  `PrepareCommitAsync`, which fetches nothing), `BranchStartAsync`, `CreateBranchAsync` with its push, every other `worktree
  prune` (`RemoveAsync`, `RemoveSeatAsync`, the sweeps), `CommitAndPushAsync`'s commit and push (not its `add -A`),
  `MergeBaseAsync`'s merge, `PushAsync`, and the fetch of
  `ChangedFilesAsync` and of a reviewer's read of a commit the clone lacks. What is slow and needs no lock is done before it
  is taken, never under it: a worktree directory is always deleted first (sandboxed, that is a run of the launch helper; also
  the directory a fresh worktree replaces, and what a failed `worktree add` left, deleted once the lock is released — the
  directory is its item's, which only that item's run or a sweep under its run lock touches, so nothing else can act on it
  half deleted; its stale registration is pruned under the lock), and the installation token of a fetch or push is minted
  first (a GitHub call) and passed in — still one fresh token per operation, never cached (`CreateBranchAsync` uses one for
  its fetch and its push), none without a `PushGrant`, and none when `CommitAndPushAsync` finds nothing to commit and nothing
  ahead of the base (read before the lock). A token minted before a wait of an hour would have expired; the push then fails
  and the run with it. No git call leaves a process behind to write the clone outside the lock: `OwnerGit` sets
  `gc.autoDetach=false` and `maintenance.autoDetach=false`, so the automatic maintenance that ends a `fetch`, `commit` or
  `merge` (`gc --auto`: `pack-refs`, `worktree prune`) runs inside the call, under the lock, not detached.
  The lock is an advisory `flock`, which any process that can open the file — read-only is enough — can take, and the work
  root's inheritable ACL entry lets the worker user read everything made under it. So, sandboxed, a `GitWorkspace` closes
  `<WorkRoot>/clone-locks` to the worker user before it takes its first lock (`IWorkerSandbox.CloseToWorkerAsync`, owner-side,
  no root and no change to the set-up script or the launch helper: `/bin/chmod -N` removes every ACL entry of the directory,
  the inherited one included, so nothing made in it later inherits one; mode `0700`; then `/bin/ls -led` must show
  `drwx------` and no ACL entry at all, `WorkerSandbox.IsClosed`) — code a worker runs (`dotnet test`, `cargo test`) cannot
  open a lock file, so it cannot hold a clone's lock against other runs. Fail closed: a directory that cannot be closed or
  does not read back closed is a broken set-up, `FactoryUnavailableException` (`the clone locks: …`, factory-wide: no lock is
  taken, no git runs, nothing is recorded against the item). Each `GitWorkspace` (each run) closes and checks it once; with
  `Worker:RunAs=none` there is no worker user and nothing is closed. Reads by object take no lock (a commit's tree, files,
  diff and grep, `ReopenAsync`, `HeadAsync`, `OverlayAsync`, which writes only its worktree's index and files). Private
  `…LockedAsync` helpers expect the lock; it is never taken twice by one call chain (a second open would wait for ever). The
  lock is per clone path, so on a case-insensitive volume two spellings of one repository share it, as they share the clone.
  Two seats at once each hold a checkout at `…/<owner>/<name>`, so git numbers the second one's admin dir (`widgets1`):
  `AdminDirAsync` finds a worktree's admin dir by its `gitdir` file, not only by name. Not changed here (later Phase 4b
  stories): the sandbox user (`DeleteAsWorkerAsync` and every helper exit still kill every `_factory` process — which is why
  each happens under the sandbox user's lock, "Worker phases").
- Workers lead their own process group (launched via `/usr/bin/perl` `setpgrp` + `exec`); the pid is checkpointed
  (`worker-started`) as soon as the process exists, and a resumed Implement stops a still-running orphan's group
  (`ClaudeWorker.StopOrphanAsync`, only if it is still a group leader running `Worker:ClaudePath`) before going on.
  Sandboxed, the recorded pid is sudo's and the owner can't signal `_factory`, so `StopOrphanAsync` instead runs a
  no-op through the helper, whose exit kills every `_factory` process (`WorkerSandbox.StopAllAsync`).
- Workers get only the router URL + router key; the worker env is an allowlist (`ClaudeWorker.BuildRouterVariables`,
  enforced again by `scripts/factory-worker-launch`).
- Worker model classes (E8, sc-25659, `Worker/WorkerModelClass.cs`, over `Router/ModelClass.cs`'s header, class and `model_class_unavailable`
  constants; reviews and second opinions run on `high`, sc-25626, below): every worker session names a router model class and no model.
  `IWorker.RunAsync` takes the class (`max`|`high`|`mid`|`low`, anything else refused before a process starts); `ClaudeWorker` sends it as a
  second `ANTHROPIC_CUSTOM_HEADERS` line, `x-weave-model-class: <class>`, after `X-Weave-Router-Key` (Claude Code splits that variable
  on line breaks; no new variable, no `--model`, no `ANTHROPIC_MODEL`, no `x-weave-force-model`). Roles, decided in code, never by a
  model: the implementer and its review and conflict fix rounds run on the item's coding class (`WorkerModelClass.Coding`: `low`
  when the story is simple — the `simple` label, any case, or an estimate of 1 or 2 points — else `mid`, no estimate included); the
  CI fixer on `mid` (`CiFix`); issue triage on `low` (`Triage`); a plan item's planner on `high` (`Planner`, sc-25870). Labels and estimate come through `IWorkSource.ReadSpecAsync`
  (`WorkStory.Labels`/`Estimate`: Shortcut's story `labels` and `estimate`; a GitHub issue item has no estimate and its labels are
  read from the issue at spec time — they only pick the class, and need not come from a collaborator, since an issue template can
  apply one; a failed read logs a warning and counts as no labels, i.e. `mid`, never an item failure). Each run of a worker session checkpoints `worker-model-class` (Detail: the class), right after
  the session's `session` row (a resumed session gets a fresh row: the class is re-derived each run). A failed session whose error
  result (`is_error`) or stderr carries `model_class_unavailable` (the router's 503 when no model of the class can serve; it never
  falls back to another class) is `WorkerResult.ModelClassUnavailable`: it pauses the factory for usage like the rate-limit backstop
  (reason `worker-model-class-unavailable`, backoff, Paused `usage-paused`, auto-resumed, same session) and never escalates; the same
  text in a non-error result is model prose and the failure escalates as usual. A class whose models are all refused by a spent
  subscription comes back as that 429 and takes the existing usage path. Claude Code's own small utility calls (titles, summaries)
  carry the same headers, so they run on the session's class too (accepted). The router key is refused if it holds a CR or LF, in
  every auth mode (it would end its header line and could add another).
  Repo settings guard (`Worker/RepoSettingsGuard.cs`): an implementing worker loads the worktree's `.claude/settings.json` and
  `.claude/settings.local.json` (`--setting-sources project,local`), whose `env` outranks the env the factory sets (it could drop the
  class, add `x-weave-force-model`, change `ANTHROPIC_BASE_URL`), as could `apiKeyHelper` or `model`. `RunWorkerSessionAsync` checks
  the files owner-side, fail-closed by allowlist: a settings file may hold only top-level keys that cannot change the model,
  endpoint, headers or credentials (`RepoSettingsGuard.AllowedKeys`: `permissions`, `hooks`, `disableAllHooks`,
  `includeCoAuthoredBy`, …); any other key (`env`, `model`, `apiKeyHelper`, unknown or future ones), a non-object or unparsable file,
  or a file `SafeFile.Read` refuses fails the round (Escalated, with the reason). `SafeFile.Read` (also behind `Taint.OfRepoSettings`,
  where a refused file taints) never follows a symlink (`.claude` itself may not be one either), opens with `O_NONBLOCK|O_NOFOLLOW`
  and refuses anything that is not a regular file (a FIFO would otherwise block the open forever while the run holds the item's
  lock), refuses a file over 1 MiB without reading past the cap, and turns any I/O or memory failure into a refusal. The guard runs
  before every implement, review/conflict fix and CI fix session (new or resumed: a branch the worker committed such a file to in
  an earlier round is refused before the next round starts) and again when the worker finishes, before `worker-done` (a worker that
  leaves one never has it pushed).
  Why not re-assert the router variables through the factory's own `--settings` (which by Claude Code's documented precedence —
  not re-verified against 2.1.291 — outranks project settings; it takes inline JSON or a file path): `ANTHROPIC_CUSTOM_HEADERS` and
  `ANTHROPIC_AUTH_TOKEN` hold the router key, so the flag would put the key either in argv (inline JSON, readable by every local user
  through `ps`) or at rest in a file the worker user can read, which the owner cannot create for `_factory` alone without root (the
  launch helper would have to write it, a helper change) — a new copy of the key for a case the guard already refuses. Re-asserting
  only `ANTHROPIC_BASE_URL` (no secret) would leave the headers, the part that carries the class, unprotected, so it is not done.
  Residual risks, accepted:
  - Mid-session settings: a worker that writes a settings file with `env` during its session, makes calls (Claude Code may reload
    it), then deletes the file before it exits is NOT refused — the finish check sees no file; only the `implementer-model`
    checkpoints (the models that answered) would show calls outside the class. A file still present at the end fails the round.
  - Front matter: project subagents and commands (`.claude/agents`, `.claude/commands`) and prompt/agent hooks (`hooks` is allowed)
    may name a model; that only asks for it in the request body, and the class header still holds the router to the class.
  - `~_factory/.claude.json` (Claude Code's global config) is not filtered by `--setting-sources`, is writable by the worker and
    persists across items; whether it can carry `env` or model overrides is unverified.
  - E8 covers the session's own calls: a worker holds the router key, so code it runs (a test, a build step) can call the router
    directly with any class or none.
  A target repo whose default branch carries refused settings gets no implementer until they are removed. Triage loads no settings
  at all (`--setting-sources ""`).
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
  (`triage`, `PromptFence.Spec`) in every worker prompt (implement, review fix, CI fix, conflict fix), and so is an item the
  factory created from a plan, on every source; an item of a trusted source (a Shortcut story, a Kanban item:
  `ItemNaming.Trusted`, the one trust decision, `WorkStory.UntrustedName`) is written on the owner's board and stays unfenced
  instructions.
  Interrupts: the taint write of a tool use runs on a never-cancelled token (`ClaudeWorker` passes `CancellationToken.None` to
  `OnUntrusted`), and before resuming a session `RunWorkerSessionAsync` replays its stored `session_events` through
  `StreamJsonState` (`WorkLedger.ReplayTaintsAsync`) and taints it for every web/MCP use found; a failed read fails the run.
  Repo settings (`Taint.OfRepoSettings`, reason `repo-settings`): before launch, and again after the worker's run (it may write
  them itself), the worktree's `.claude/settings.json` and `.claude/settings.local.json` (the `project,local` sources) are read;
  `hooks`, `enableAllProjectMcpServers`, `enabledMcpjsonServers`, a command helper (`apiKeyHelper`, `awsAuthRefresh`,
  `awsCredentialExport`, `otelHeadersHelper`, `statusLine`), any `permissions.allow` rule not in `ClaudeWorker.AllowedTools`, or a
  `.mcp.json` naming a server taints the session (a new one from its start; a resumed one is refused). Unreadable or unparseable
  settings taint too. Residuals: an orchestrator SIGKILLed after the worker wrote a tool_use line but before the line was stored
  (pipe buffer / capture queue) leaves nothing to replay. Accepted: the allowed `dotnet build/test/restore` and `cargo
  build/check/test/fetch` (below) run repo and package code (MSBuild targets, analyzers, cargo build scripts and procedural
  macros, tests, restore) whose output the worker reads; it runs sandboxed with no credential but the
  router key, is the code under review or a pinned dependency, goes back through review and CI, and tainting it would leave no
  worker able to push.
  Worker tools (`ClaudeWorker.AllowedTools`, what an implementer, review fixer, CI fixer and conflict fixer may use): `Read`,
  `Edit`, `Write`, `Glob`, `Grep`, `Bash(dotnet build:*)`, `Bash(dotnet test:*)`, `Bash(dotnet restore:*)` and — sc-25877, a
  coordinator decision of 2026-10-13 pending the owner's confirmation (list below): without them a worker on a Rust repository
  could never compile or run a test, and "plan → merged code" there depended on blind code plus CI fix rounds —
  `Bash(cargo build:*)`, `Bash(cargo check:*)`, `Bash(cargo test:*)`, `Bash(cargo fetch:*)` (`ClaudeWorker.CargoTools`). The
  prompts name both stacks (`RunPipeline.BuildsAndPasses`). Each is a Claude Code prefix rule: the command must start with the
  words before `:*`. So a headless worker is denied `cargo run`, `install`, `publish`, `login`, `owner`, `yank`, every other
  subcommand, and any form whose second word is not the subcommand (`cargo +nightly test`, `cargo --config … build`); a target
  repository whose settings allow one of those (`Bash(cargo:*)`, `Bash(cargo run:*)`, `Bash(cargo test*)` …) taints the session
  as before, while the four exact rules no longer do (they are in `AllowedTools`). Aliases: an `[alias]` in a repository's
  `.cargo/config.toml` cannot rebind `build`, `check`, `test` or `fetch` (cargo ignores an alias that shadows a built-in command,
  with a warning), so the four names mean cargo's own commands. Residuals, accepted, the same as the `dotnet` rules': (a) what
  follows the prefix is the worker's — `--manifest-path` or `--target-dir` outside the worktree, `--config key=value`, `-- <test
  arguments>` — as `dotnet build <any project>` and `-p:` properties are; (b) the four commands run code the repository and its
  dependencies supply (build scripts, procedural macros, tests, a `runner`, `rustc-wrapper` or `[env]` from the repository's or
  `~_factory/.cargo`'s config), as the sandbox user with no credential but the router key, with the network `cargo fetch` and
  that code have; (c) the word boundary after the prefix is Claude Code's own matching: if a release matched `cargo testx` under
  `cargo test:*`, a repository alias or an external `cargo-testx` on the worker's PATH would run — still only repository code in
  the same sandbox, the residual of (b), no more; (d) cargo must be on the worker's PATH (the launch helper's fixed one,
  unchanged: `/opt/homebrew/bin` or `/usr/local/bin`) — where it is not, the commands fail and the worker is where it was before.
  The result goes back through review, CI and the gate. Triage and the planner gain none of this: `WorkerTools.ReadOnly` has no
  allow rule but its read rule and denies `Bash` by name.
- The orchestrator pushes only to `factory/*` (items' branches, and a plan's `factory/changeset-*` branch, sc-25872), with a repo-scoped GitHub App installation token passed
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
  `-c core.hooksPath=/dev/null`, `core.fsmonitor=false`, `rerere.enabled=false`, LFS filters emptied, `gc.autoDetach=false` and
  `maintenance.autoDetach=false` — no detached maintenance outside the clone's lock, sc-25987), so no hook, filter or
  merge driver a PR's files or `.gitattributes` select runs as the owner; worktrees live under the owner-owned work root (root-owned parent), are shared with
  `_factory` by an inheritable ACL set on the empty directory before checkout (never `chmod -R`, which follows
  symlinks), and are deleted (as `_factory` first) once the PR is open or the item escalates. Only a paused or
  crashed Implement or fix round keeps its worktree for resume; a worker that won't stop marks its exception
  (`WorkerStillRunning`) so nothing deletes under it. Each run sweeps worktrees no run will resume, never one a
  live run owns (sc-25987, above).
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
  A plan item's stories are listed beneath its row, from the ledger alone (sc-25876, above).
  Board links and epics (sc-25985): a row may carry `PipelineRow.Links` (`BoardLink`: the item's and its epic's link), which
  `DashboardData` takes from each source's `IBoardLinks` (`WorkSourceKind.Links`, `WorkSourceRegistry.BoardLinks`; built whether
  or not the source has its credential) — read from the ledger alone, never a board. Kanban's is `KanbanLedgerLinks`: the item's
  GUID and project (`kanban_items`) and its epic's (`kanban_epics`, by `WorkItem.EpicId`) through the one link builder,
  `KanbanLinks`, on `Kanban:WebUrl`. KanbanBoard's web UI has no page of one item: the link (`?project=<id>[&epic=<id>]&item=<id>`)
  opens the board of the item's epic, or of its project when it has none (the `item` parameter only tells links apart); an item
  whose project the ledger has not recorded yet (a key row from before sc-25985) is shown without a link until it is next listed.
  A row with links shows its id as that link and `epic <kb-N>` beside the title; Shortcut and issue rows have no `IBoardLinks`
  and are unchanged. `PipelineRow.Epic` (`ItemNaming.EpicOf`) is what the page's epic control rows are keyed by, so a Kanban epic
  and the Shortcut epic of the same number are two rows, each form posting its own scope (`epic:kb-5`, `epic:5`).
  Runs in flight (sc-25991): every row has a Run cell (`PipelineRow.Run`, `RunView`) — whether a run of the item is alive, its
  phase, and its worker slot — so several running items show at once, each as what it is. Design decision, where this comes
  from: the LEDGER, never the intake loop's memory, so the page is right for the runs of any process (a `factory run` beside
  `factory work`) and after a restart. (a) Phase = the state in words plus what the item's own rows say it is at
  (`RunView.PhaseOf`: `implementing`, `review`, `waiting for CI`, `merge queue: waiting for its turn` / `its repository's turn`
  by `MergeQueue.QueuedRow`/`InTurn`, `waiting on its stories`, `paused in Implement: freeze-paused` — the paused-from state
  and the pipeline's own reason). (b) The item's slot = `RunPipeline.SlotOf` (its `slot` row; slot 1 for a `worker-started`
  with none). (c) Its use of the slot = the last of three checkpoints `RunPipeline.InWorkerPhaseAsync` writes (`Ledger/SlotUse.cs`;
  Detail `<slot|any> <user|-> <what>`, e.g. `2 _factory2 implement`; typed outcome `passed` — a wait is no failure): `slot-wait`
  once when a run starts waiting for a slot another run's worker phase holds (never per poll of the wait; `any -` for an item
  with no slot yet), `slot-held` when an outermost worker phase has the slot's lock (a phase inside a phase writes none), and
  `slot-released` when the phase ends, however it ends, or the wait ends without the slot (`not taken: …`; a Pause, a Stop, the
  freeze) — written on no cancellation token, bounded, and a failed write only logged. So a worker phase costs two rows (three
  after a wait), in every sandboxed factory, one slot included; none unsandboxed (`Worker:RunAs=none`: there is no slot), none
  for a stop's wait and none for a triage (it has no item: an item waiting on a triage's slot shows as waiting). These rows are
  not work of the attempt: `RunPipeline.CurrentWorkerAttempt` leaves them (and the `slot` row) out, so an attempt holding only
  them is a fresh one — Implement and a fix round do not take it for a resume, look for a worktree that never was and record
  `worktree-lost` (which a multi-slot factory's `slot` row did before this). (d) Liveness: a row says what a run last
  recorded, not that the run is alive — a run that crashed or was killed inside a phase leaves `slot-held` as its last word. So
  `DashboardData` also asks which items' run locks are held (`liveRuns` = `PostgresRunLocks.HeldAsync`: `pg_locks`, the
  single-key advisory locks of this database with a positive key — nothing is taken, so asking never makes a run find its
  item locked; a dead run's lock is gone with its connection), after reading the rows. `RunView.Live` true: `running: <phase> ·
  holds slot 2 (_factory2) to implement` or `· waiting for a slot to implement` / `waiting for slot 2 (_factory2) to …`; false:
  `no run now: <phase> · slot 2 (_factory2), not held`, and a held or waiting last row without a live run is said as stale, in
  the error colour (`no run now: the last one ended holding slot 2 (_factory2) without recording it (crashed or killed)` —
  only what is known: not whether the slot is free now, nor what the next run does); null (a reader given no lock reader:
  tests, or a `pg_locks` read that failed — caught, so the page is not marked stale for it) shows the rows' word. A hold or
  wait is the run's now only while no transition follows its row (`RunView.Ended`: a phase records its next state just before
  it gives the slot up, so a dangling `slot-held` behind a later `Review` is an earlier run's; for that moment of a live phase
  the cell says `not held`), and of two items shown holding one slot only the newer hold is (`RunView.Displaced`: the other
  is stale, `another item's run holds slot 1 (_factory) now: an earlier run of this one ended holding it without recording it
  (crashed or killed)`). And a run's start, under the item's run lock (`RunAsync` and `HoldAsync`), ends on the record a
  `slot-held`/`slot-wait` an earlier run left as the item's last slot-use row (`EndEarlierSlotUseAsync`: `slot-released`
  `<slot> <user> <what> not recorded: ended with an earlier run`, bounded, only logged on failure). Slot bookkeeping rows
  (`slot`, `slot-left`, `slot-wait`, `slot-held`, `slot-released`: `SlotUse.IsBookkeeping`, one predicate) are never "the
  item's last row" for a reader by position: the escalation comment's `Last ledger state` (a phase's release is written in
  its `finally`, before the Escalated row), `GiveUpAsync`'s "while that `plan-held` row is still its last", and a worker
  attempt's rows (`CurrentWorkerAttempt`). A row shown alive is read again every 15 s (a death writes no row, so no NOTIFY).
  Residuals, accepted: a clean-up that takes an item's run lock for a moment to delete its leftovers (`LiveRuns`) shows as a
  run for that moment; a helper that outlives its phase keeps the slot's lock (`WorkerLock.KeepUntil`) though the page says
  released — the `[lock] … stays held` log line is that case's record. What a one-slot installation now sees (E9): one more
  column, e.g. `running: implementing · holds slot 1 (_factory) to implement`, then `no run now: review · slot 1 (_factory),
  not held`; and two more rows per worker phase in its ledger (no report, metric or decision reads them).
  Tests: `RunsInFlightTests` (two real runs: both holding in two slots; one holding and one waiting with one slot; a dead run's
  stale row; a dangling hold behind a later transition, ended at the next run's start; two items holding one slot; a failed
  liveness read; `RunView`'s phase and slot words), `DashboardComponentTests` (two running rows; the deferral list),
  `LedgerPostgresTests` (`HeldAsync` across lock holders, taking nothing).
- Review → CI → MergeGate → Merge (sc-25378; `RunPipeline.Gate.cs`, only in a pipeline built with a `GateStage`, which
  production always is): Implement records every model that answers the implementer (`implementer-model` checkpoints, from
  each assistant message's `model`; a record only — the panel does not depend on it). Review is a panel (sc-25379, `Gate/ReviewPanel.cs`): `correctness` and
  `spec-conformance` always, `security` only when the diff touches a path whose tier in the base branch's
  `factory/gate.yaml` lists `security-review` or that the code floor `RiskyPaths` matches (`GatePolicy.SecurityReviewReasons`,
  both recorded in the verdict's risky paths; see the floors under MergeGate below; a missing/invalid policy escalates at
  Review before any call). Every review and second opinion runs on the router's `high` model class (owner decision
  2026-10-09, epic E8, sc-25626; `ReviewModels`, `Router/ModelClass.cs`): no model id is pinned in code or config, the router
  picks the model inside the class, and any model may review or confirm — including the one that wrote the code, or the
  one that served the review a second opinion checks (there is no family, version or independence rule). Each role makes one
  router call (`RouterReviewer`: `POST /v1/messages` with `"stream": true` — the router cancels a call that has sent its
  client nothing for 10 s — assembled by `MessageStream`, which fails closed on an `error` event (a transient `server_error`/`api_error` one after the turn is retried, below), a stream without
  `message_stop` or one silent for 2 min; router key only, `x-weave-model-class: high` and never `x-weave-force-model` (the
  router refuses the two together), body `model` the fixed placeholder `ModelClass.RequestModel` (`default`: no catalog model
  has it, so no passthrough lane serves it verbatim; the router's in-class pick serves the call — the factory's router key
  must therefore have no routing-policy caller passthrough or blind-experiment passthrough arm, which 503s
  `model_class_unavailable` for a body model outside the class and would usage-pause every review in a loop), its own fresh
  `X-Claude-Code-Session-Id` so the cost stays scoped to it and no worker session or transcript reaches it) whose system
  prompt is the role's prompt file and whose message holds the story, the base commit's file list
  (`IGateGitHub.GetFilesAsync`) and the diff of the PR's head commit — nothing else. Tool loop (sc-25705, sc-25706,
  `Gate/ReviewTools.cs`): every role and every second opinion is offered exactly `ReviewTools.Names` (E2, a read-only allowlist
  in code; a tool name off it — CodeGraph's Ask, `project_report`, `rag_search`, … — answers an error result and is never
  forwarded, and `CodeGraphMcpClient.CallAsync` refuses one before sending anything). Repository tools, `IReviewFiles` served
  owner-side from the gate's clone (`GitWorkspace`, by object, `OwnerGit`-isolated: no checkout, so no filter, hook or symlink of
  the PR runs or is followed; a commit not yet in the clone is fetched first): `read_file` (`git ls-tree -l` + `cat-file`; a
  symlink/submodule/directory is no file, one over `MaxFileBytes` 1 MiB is not read; the blob is read as bytes, and a NUL byte or
  invalid UTF-8 — so UTF-16 too — is binary, "binary file, not shown"; a valid U+FFFD is text), `list_files` (`ls-tree -r`, at most
  `MaxListedFiles` 2,000 shown) and `grep` (`git grep -I -E --no-textconv -e <pattern>` on the tree, pattern ≤ `MaxPatternChars` 200,
  `MaxGrepPerFile` 20 per file, `MaxGrepMatches` 200 shown, lines cut at `MaxGrepLineChars` 300), each at the PR head or, with
  `ref: base`, its base commit. Their git reads stream git's output and stop git (its whole process tree) once the shown cap is
  passed ("More than N …, the first N shown"), a listing passes `GitWorkspace.MaxReviewReadBytes` (240,000) or grep's kept lines
  do (each line keeps at most `MaxGrepRecordBytes` 8 KiB); each git command of a read has its own `GitWorkspace.ReviewReadTimeout`
  (30 s) and answers an error past it; a cancelled git read (`RunGitAsync` included) stops the git it started before it throws; a path is normalised (`RepoPath`) and
  one leaving the repository is refused before anything is read (`--literal-pathspecs`: no glob or magic). CodeGraph tools
  (`ReviewTools.CodeGraphTools`, each checked against CodeGraph's source to reach only the graph store and indexed files, never a
  model — the reason per tool is the comment on the list): `analyze_impact`, `search_graph`, `trace_call_path`, `find_consumers`,
  `find_publishers`, `get_code_snippet`, `read_node_source`; forwarded owner-side to CodeGraph's MCP endpoint
  (`CodeGraph/CodeGraphMcpClient`, one MCP session per call, ended by a best-effort `DELETE`), arguments built by the orchestrator
  (strings cut at 300, depths clamped 1–5, a snippet path `RepoPath`-normalised). No tool takes a project: the orchestrator always
  sets the PR's repository, the CodeGraph project whose `search_projects` entry's `Repo:` URL is exactly
  `https://github.com/<owner>/<name>` (with or without `.git`, case-insensitive; resolved once per session), and a repository no
  entry matches answers the error "repository not indexed in CodeGraph". Queries are asked about this repository; answers are not
  filtered and may name nodes of other indexed projects that depend on or call it (`analyze_impact`'s cross-repo impact,
  `trace_call_path`, `find_consumers`/`find_publishers` cross projects), which every graph tool's description says; `read_node_source` (whose CodeGraph handler takes only a
  node id) is shown only when its answer's header names that project. Each answer is labelled with the commit its index
  describes, read only from CodeGraph's contract (CodeGraph sc-25702): `structuredContent.commitSha`, else a first text line
  `Commit: <40-hex sha>` — otherwise "commit unknown" (today's hosted answers carry neither until sc-25702 deploys). Head overlay
  (sc-25708, `Gate/ReviewOverlay.cs`, E3): when a review starts — once per head, before the first panel call, only if a role is
  reviewed there (not when every review is carried) — the orchestrator, never a model, asks CodeGraph for an overlay of the PR
  head (`ReviewOverlays`: `request_overlay(repo = the pinned project, ref = head SHA)`, then `get_overlay_status(overlayId)`
  every `ReviewOverlays.DefaultPollInterval` (10 s); CodeGraph C6, michaeltrefry/CodeGraph PR #75, whose shapes the fakes copy) and
  waits at most `CodeGraph:OverlayTimeoutMinutes` (the calls included), a Pause/Stop checked before each status read like before
  each panel call. The outcome — `ready` (status ready, not `stale`, and `headSha` exactly the head), `failed` (failed/expired with
  its `structuredContent.overlayError`, a ready overlay of another commit, or one still `stale` — its base no longer the
  default-branch index commit, which read tools will not use — after one new `request_overlay`, which CodeGraph answers with a
  fresh overlay: "the overlay is stale (default branch moved past its base)"), `refused` (HTTP 403 `tool_not_entitled` from
  CodeGraph's entitlement middleware, `McpHttpException`: "not entitled to request_overlay"; or an error answer,
  `structuredContent.error.code` in the reason: `repo_not_found`, `invalid_ref`, `overlays_unavailable`, …), `timed-out`,
  `unavailable` (no token, repo not indexed, CodeGraph unreachable or the request/lookup timing out in its HTTP client, or a
  `temporarily_unavailable` answer — requested again every poll interval — still so at the timeout; a status read that fails or
  times out is read again until the timeout) — with overlay id, head and base, is the verdict's `codegraph`, a line of the PR
  report and a `codegraph-overlay` checkpoint (Detail: its JSON; outcome passed when ready, else failed); a review resumed on the
  same head (crash, Ctrl-C, a pause) reuses a recorded `ready` instead of waiting again (other outcomes are retried). A Pause, Stop
  or shutdown still ends the wait by throwing; none of the outcomes fails the review (E4). A head-pinned call CodeGraph answers with
  an error naming no commit is labelled "CodeGraph's answer for the PR head <sha> (error)". Ready, every CodeGraph call of every role and second opinion passes `sha` = head
  (C4, CodeGraph PR #71) and only an answer naming exactly the head is labelled "CodeGraph's index of <project> at the PR head";
  a typed not-indexed answer (`structuredContent.notIndexed`, which names the head yet carries no data) makes that call fall
  back to the default-branch call, labelled and recorded (`tools[].fallback`). Not ready, the calls go without `sha` and are
  labelled "…'s default branch as of <sha>, not the PR head" (`ReviewTools.DefaultBranchLabel`, which the loopback proxy prepends
  to every CodeGraph answer it relays too, without a head and so without "not the PR head": both seams name the commit the same way). `request_overlay`/`get_overlay_status`
  (`CodeGraphMcpClient.OverlayTools`) are sent straight through the MCP client and are on no allowlist, profile or tool list (a
  test asserts it). Triage is unchanged (no `sha`). Overlay node ids are negative, so `read_node_source` takes any integer id. Kanban tools (sc-25707, only with `Kanban:Token`): each session lists the Kanban upstream once
  (`ReviewTools.DefinitionsAsync`) and is offered, after `ReviewTools.Names`, its tools on `McpServers.KanbanTools` with their own
  input schemas (a failed listing offers none; a tool whose schema is not a JSON object of `"type": "object"` within
  `ReviewTools.MaxKanbanSchemaChars`, 4 KB serialized, is left out and logged once, `ReviewTools.UsableSchema`), plus one
  prompt line saying so; a call is forwarded owner-side through the same
  `McpUpstreams` registry the loopback proxy uses (input ≤ `ReviewTools.MaxKanbanArgumentChars` 2,000, not repo-scoped: Kanban
  has no repository mapping), and a Kanban write tool is on no allowlist, so it answers an error and is never forwarded. A turn stopping for `tool_use` has the orchestrator run each call and answer it in a `<tool-result>` fence in the
  next turn of the same session (same session id, class header, placeholder model), at most `RouterReviewer.MaxTurns` (8) turns
  with tools (`tool_choice: auto`) and `MaxToolCalls` (24) calls; a model still asking for tools in the 8th turn gets those calls
  answered "not run" plus a final-answer request (`RouterReviewer.FinalAnswerRequest`) and one more turn with `tool_choice: none`
  (the tool definitions stay: the Messages API refuses tool blocks without them); no findings/confirmation line there — or tools
  asked for again — is unusable ("No usable final answer after the turn cap: …"). The session's tool results together are at most
  `ReviewTools.Budget` characters (`MaxSessionChars` 200,000 less the prompt, at least `MinSessionBudget` 8,000), and each result at
  most `MaxResultBytes` (60,000) UTF-8 bytes, cut on a character boundary with an explicit `[cut: N bytes, the first M bytes shown]`
  marker; a call once the budget is spent answers an error. Every turn must be served on `high` (the first that is not ends the
  session, unusable), a usage refusal on any turn pauses as below, a turn whose stream ends in an upstream `error` event of type
  `server_error` or `api_error` (`MessageStream.TransientErrorTypes`; seen live from OpenAI on the high class; not a usage refusal —
  `overloaded_error` stays one) is sent again — the same request, session id and class header — at most
  `RouterReviewer.MaxTransientRetries` (2) times after a short backoff (2 s, then 4 s), each failed attempt's error recorded on that
  turn (`turns[].retried`), and fails closed as any broken stream when every attempt fails (sc-25710), and a router 400/413 on a later turn (a conversation grown too
  long) makes the answer unusable rather than failing the run. A tool that cannot answer (no such file, a binary file, a
  repository CodeGraph does not index, CodeGraph unreachable or unconfigured) gives the model an error result; it never fails
  the review by itself. Each call is recorded in the verdict's review or, for a second opinion, on the finding's confirmation
  under its own `confirm-<role>` session (`tools`: tool, arguments, SHA-256 and UTF-8 size (`bytes`) of the raw result before
  fencing and bounding, error, CodeGraph commit; `turns`: every turn's number, served model and served class, the final-answer turn
  included — the review's own `served`/`class` stay the last turn's; both absent in a verdict recorded before sc-25710, which still
  reads) and the reports (PR body, closeout, escalation; `LedgerReport`) show per review its calls, how many answered an error and
  up to `LedgerReport.MaxFailingToolsShown` (3) distinct failing tool names (inert code spans), and the same for its second
  opinions (E4/E8). The prompts make reading code a rule: no claim about code outside the diff without reading it
  (a finding about unread code is not allowed); the correctness prompt also requires, before a pass, one CodeGraph
  `analyze_impact`/`trace_call_path`/`find_consumers` query for each changed non-private symbol (at most the 5 most significant),
  naming the callers found, or saying CodeGraph answered an error or not indexed and going on (sc-25710); and a second opinion on a finding that depends on unshown code reads it, then
  confirms or rejects. Prompts: `factory/prompts/{correctness,spec-conformance,security,confirm}.md` in this repo, compiled in as
  embedded resources (`ReviewPrompts`) — never read from the target repo or the PR (which could rewrite the prompt it is
  judged by) nor from disk at run time; changing one is a dark-factory PR. Each role answers findings tagged `blocking` or
  `optional` (an unknown severity counts as blocking); each blocking finding goes to a second opinion (its own high-class router session) with `confirm.md`: not confirmed → downgraded to optional (`downgraded: true`), does not
  block; confirmed, or an unusable confirm answer → stays blocking. Every panel call's session id is generated by the
  pipeline and checkpointed (`review-session`: "session class sha role prompt-path@sha256:hash", class always `high`; role
  `confirm-<role>` for a second opinion) before the call, so a call that never returns still has a readable cost (E9) and the prompt it used is on
  record; it never goes in a row's `ClaudeSessionId`, which stays the implementer's (outcomes, escalation comments and
  stops read the last one). A Pause/Stop is checked before each panel call. The verdict (`ReviewPanel.Decide`,
  deterministic: pass only when every required role answered cleanly and no finding is blocking after confirmation; it
  lists the risky paths and, per review and per second opinion, the served model (`served`, for reporting, from the
  stream's `message_start`) and served class (`class`, from the `X-Weave-Model-Class` response header), session, prompt and
  findings) is a `verdict` checkpoint bound to that head SHA. Anything but a clean findings line served on the `high` class
  is an unusable review and a fail; a second opinion not served on it is unusable (the finding stays blocking). A missing or
  blank class header fails closed (E2). The router's `model_class_unavailable` 503 (no high-class model can serve; it never
  falls back to another class) and the class's spent-subscription 429 are usage refusals: they pause the factory
  (`UsagePause.ReviewerRateLimited`) and never escalate. Fix loop (sc-25380, `Gate/FixLoop.cs`, `FixAsync`): a fail whose
  only cause is blocking findings every one of which a second opinion confirmed (`FixLoop.Fixable`; a review or confirmation not served on the high class makes it unfixable) goes Review → Fixing (row
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
  against; `GatePolicy`, `version: 2`, `3` or `4`, sc-25381: path tiers `sealed`/`protected`/`free` with `paths` and `checks`, `normal`
  with `checks` only, and `risk: {max_changed_lines, max_changed_files, max_fix_rounds}` — version 3 (sc-25709) adds two required
  risk keys, `max_blast_radius` (`low`|`medium`|`high`: a maximum, a file over it fails; `critical` would allow a critical file, is
  looser than the code floor and invalid) and `max_hotspot` (`off`, or a number ≥ 0: the opt-in limit on the hotspot score; `off`
  is the explicit opt-out, chosen over an optional key so every key stays required); each version's key set is strict, so a CodeGraph
  key in a version 2 policy is unknown; version 2 still parses and runs at the floor with no hotspot limit; version 4 (sc-25873)
  adds two more required risk keys, `max_changeset_lines` and `max_changeset_files` (whole numbers ≥ 1: the size limits of a plan's
  changeset pull request, unknown and so invalid in versions 2 and 3; "Changeset size" above); every key required;
  version 1 is rejected; missing/invalid/unreadable → no merge, escalate; no bypass key exists). The policy can tighten the gate but not
  loosen it below floors in code (E2; described once on `GatePolicy`): checks — every tier lists `ci-green` and
  `review-pass`, sealed/protected also `security-review`, protected/normal also `risk-threshold` and
  `new-tests-fail-on-base` (`GatePolicy.FloorChecks`);
  sealed — the tier must match the policy and CODEOWNERS (root, `.github/`, `docs/`) and cover `.github/workflows/` and
  `factory/prompts/` with a pattern matching everything under them (`PathPattern.CoversEverythingUnder`: a directory
  pattern or trailing `**` naming the directory or an ancestor; naming files in it is invalid); security review — a path
  whose tier lists `security-review` or that `RiskyPaths` matches (scripts, build/container files, dependency manifests, Rust's
  `build.rs`, `rust-toolchain*` and `.cargo/config*`,
  keys/.env, security-sensitive names; case-insensitive) needs it, so the policy can add security-review paths but not
  remove the floor. The gate also reads the PR, the diff of the head against the base (`IGateGitHub.GetDiffAsync`;
  unreadable → blocked; a file count — one per `diff --git` header, a rename once — other than the PR's `changed_files` →
  blocked as incomplete), the head's CI, the ledger's verdicts and fix rounds. Every path the diff touches (`DiffPaths`:
  both sides of a rename, deletes, C-quoted names unquoted, header-looking hunk lines ignored) is normalized (`RepoPath`:
  `./` and `..` resolved; one escaping the root, or an unparsed non-empty diff, is sealed) and put in the first of sealed >
  protected > free whose patterns (`PathPattern`: root-anchored, `*`/`?`/`**`, trailing `/` = directory; sealed and
  protected case-insensitive, free case-sensitive, so case only moves a path to a stricter tier) match, else normal; the change
  needs the union of its tiers' checks: `ci-green`, `review-pass` (the head's verdict passes, holds every required role, and
  every review and second opinion was served on the router's `high` model class — its recorded `class`, a missing one
  failing closed — `ReviewModels.Problems`; which model served, and the implementer's models, are not consulted), `security-review` (the verdict has the
  security review), `risk-threshold` (changed lines, files — renames count both paths; a changeset PR under a version 4 policy
  against `max_changeset_*` instead of `max_changed_*` — and fix rounds within `risk`, and CodeGraph's inputs, below), `new-tests-fail-on-base` (sc-25382, below). Risk score (sc-25709, `Gate/CodeGraphRisk.cs`; CodeGraph
  C7, sc-25735, not deployed yet — the fakes follow its contract and C4's `CommitAnswer` shape, PR #71): `EvaluateGateAsync` first
  evaluates the gate without CodeGraph (`MergeGate.Evaluate(…, riskUnread: true)`: a change that needs `risk-threshold` and that
  nothing else blocks is `Pending`, never Merge), so a change another rule blocks (sealed path, failed verdict, new tests, size)
  escalates without a CodeGraph call; only then the pipeline (`RunPipeline.CodeGraphRiskAsync`) reads, owner-side through
  the CodeGraph client (`CodeGraphRiskReader` over `ICodeGraphRisk`, the project pinned by exact repo URL), for every touched file
  (`MergeGate.TouchedFiles`: both sides of a rename, deletes) `analyze_impact_file(repo, path, sha = head, depth = 3)` — only once
  CodeGraph's overlay of exactly the head is ready (a ready `codegraph-overlay` checkpoint for that head, e.g. the review's, is
  reused until a read answers C4's `notIndexed` — e.g. status `stale`, the default branch moved past its base:
  `CodeGraphRisk.OverlayUnusable` — after which the next read requests the overlay again, `ReviewOverlays.WaitAsync` re-requesting a
  stale one; otherwise requested and waited for like the review's and checkpointed, an outcome the last row for that head already
  records not written again) and only an answer naming the head (E3) and the file asked about (its `path`, normalized like the
  diff's) — then `file_hotspot(repo, path)` from the default branch only (`source: default-branch`, the asked `path`), recorded with
  the commit it names (E3's exception) as C7 (michaeltrefry/CodeGraph PR #76) shapes it: `commitSha` is the metrics' commit and
  `indexCommitSha` the current default-branch index commit; a file without metrics (`found: false`, e.g. new) answers `commitSha`
  null and counts as 0 recorded at `indexCommitSha`; a found file with a null `commitSha` (metrics computed before CodeGraph recorded
  commits) keeps its score, recorded at `indexCommitSha` and labelled "vitals commit unknown; index at <sha>"; an answer naming
  neither commit is unreadable. Each answer is reused within one wait while the ready overlay's base is its index commit
  (`HotspotCache`, keyed by `indexCommitSha` — the commit the lookup names). The reader stops at the first input it cannot read. "The hotspot score" is CodeGraph's `riskScore`. The blast
  radius has a code floor (`GatePolicy.BlastRadiusFloor` =
  high: a critical file always fails; seam `policy-floor:blast-radius`), `max_blast_radius` can only lower it. CodeGraph rates a file critical only for a non-test dependent in another repo (depth 1, or depth 2 over a message edge) or a non-test depth-1 message consumer (`CONSUMES`, built only for repos in its `TrustedDotnetRepositories`); in-repo callers rate high at most, so in practice the floor fires on cross-repo dependents and
  consumers (docs/acceptance.md, P3-AT4a); hotspots have no
  floor — recorded and reported, failing only above an opted-in `max_hotspot`. A file CodeGraph has no data for (`found: false`,
  e.g. a new file) counts as blast radius none / hotspot 0; a file read over a limit blocks at once, even when another could not be
  read; one CodeGraph cannot read (an error answer, HTTP 403 `tool_not_entitled`, unreachable, the repo not indexed, a C4
  `notIndexed` answer, no ready overlay of the head, an answer for another commit, another file or without a commit) otherwise
  makes the decision `Pending` (owner decision 2026-10-10): `EvaluateGateAsync` reads every fact again every `Gate:CiPollSeconds`
  like pending CI (controls checked and the PR read again between reads — found merged → recorded as found merged, closed →
  escalate, a new head → Review — so the wait never holds the queue's turn for an old head; pending evaluations are not
  checkpointed). One deadline bounds the whole wait, CodeGraph's overlay wait and calls included (a `CancellationTokenSource` on
  `GateTime`): at `Gate:CiTimeoutMinutes` it is recorded Blocked and escalates, even mid-call; a real Pause/Stop still propagates.
  No CodeGraph configured (no token: `GateStage.Risk` null) fails the check at once, wherever it is required — so a factory without
  a CodeGraph token merges no normal or protected change; the reason names the setting, and `factory run` and `factory work` warn
  at start-up (`FactoryRunner.CodeGraphTokenWarning`). Inputs read for another head or leaving a touched file out block. The inputs
  are in the `gate` checkpoint's Detail after ` | ` (`CodeGraphRisk.Describe`): every file over a limit, the worst and the hottest,
  then the first `CodeGraphRisk.MaxListed` (20) others with "… N more", each hotspot with its commit, and what could not be read
  (capped likewise), so one row stays inside the reports' budget; a failure's
  reason names each file over the limit with its blast radius (or hotspot score), so the escalation comment and the PR report (both
  from the ledger) name it. The gate sends `analyze_impact_file`/`file_hotspot` (`CodeGraphMcpClient.RiskTools`) straight through the MCP
  client. They are on no reviewers' tool list and not in the triage profile; since sc-25874 they are on the planner profile's
  list (`PlannerCodeGraph.Tools`), reachable by a planner session through the loopback proxy only, without a `sha` (a test asserts
  each half: `CodeGraphRiskTests.The_risk_tools_are_on_no_reviewer_or_triage_tool_list_only_the_planners`). A sealed path always escalates (even with no verdict); a protected one escalates after its checks instead of merging; each
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
  on just its new tests (`--filter-method` per test + xUnit's own XML report under Microsoft.Testing.Platform per
  `global.json` — `--report-xunit-xml`, falling back to xunit.v3 < 4's `--report-xunit` when the runner exits 5 (rejected
  command line); never xUnit's TRX, which since xunit.v3 4.0 an unpaired surrogate in a failure message leaves empty
  (sc-25668) — else VSTest `--filter FullyQualifiedName=…` + `--logger trx`; cases joined by type + method, never display
  name; only the run's own format is read, `*.xunit.xml` or `*.trx`; an unreadable or over-50 MB report makes the check
  an `error` naming the file and why) through `SandboxTestRunner`: sandboxed exactly
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
  Rust/cargo (sc-25875, `Gate/CargoNewTests.cs`, a second `INewTestStrategy` beside `XunitNewTests`; `NewTestsCheck.Strategies`
  runs both, each for its own files, so a .NET change is judged exactly as before and a repo with both stacks by both). Found
  by syntax, nothing compiled: `Gate/RustSource.cs` is a lexer-level reader (no Rust parser ships with .NET) that knows line and
  nested block comments, string/byte/C strings, raw strings (`r#"…"#`), character literals as against lifetimes and raw
  identifiers, and reads module bodies only — inline modules and braced item macros (`proptest! { … }`) are read, function,
  `impl`, `trait` and `macro_rules!` bodies skipped; unbalanced source throws and the file is unisolable (never "no tests"). New
  tests: functions with a test attribute (`#[test]` under any path — `#[tokio::test]`, `#[sqlx::test]` — `#[rstest]`,
  `#[test_case(…)]`, `#[test_matrix]`, `#[quickcheck]`, `#[proptest]`) in the head versions of a cargo package's changed `.rs`
  files (the nearest `Cargo.toml` with `[package]`; workspaces at any depth), in `tests/` or `src/`, whose id no base version of a
  changed file has. Id = `package:target:path` as libtest names it: `words:lib:count::tests::blank`, `tool:bins:tests::runs`,
  `words:test/api:deep::nested` (`tests/api.rs`, or `tests/api/main.rs` and its modules); a module file is the library's when
  `src/lib.rs` declares its top module (or is all there is), else the binary's. A function with cases (`#[test_case]`, rstest
  `#[case]`/`#[values]`) is one test `…::name::*` (every case under it; new cases on an existing one are unisolable, like a
  theory's rows). Not new: a renamed file's test the old file had, and a test moved unchanged from where the PR removes it (ids are
  path-based, so a pure move would otherwise look new). Overlay (the PR's test code applied to the base), under one rule: a new
  test may count as not building on the base (`not-built`) only for something the base lacks, never for an import, helper or
  attribute the overlay left out — where the gate cannot apply test code faithfully the check is `unsupported`. Test code is
  what a test-only `cfg` gates, the predicate evaluated, not searched for the word (`RustSource.IsTestOnlyCfg`: `test`, `all(…)`
  with a test-only operand, `any(…)` whose operands are all test-only, never `not(…)` — so `cfg(all(test, not(miri)))` is test
  code and `cfg(any(test, feature = "x"))` production code), on the item or as an inline module's inner `#![cfg(test)]`.
  Everything changed under a package's `tests/` and every file of a `#[cfg(test)]` module (`#![cfg(test)]`, or declared
  `#[cfg(test)] mod x;` at any level above) comes whole from the head; a production file with inline tests is written — through
  owner-side git, as the retries' files are (`NewTestSet.BaseReplacements`) — as its **base** version with the head's test code
  grafted in (`CargoNewTests.Graft`): per module the two share, every `#[cfg(test)]` module replaced by the head's or added
  (removed when the head has none), whole, so its helpers and `use`s come along and the production code stays the base's.
  Imports are not test code but a test reaches them through `use super::*`: every single import of the head's `use` and
  `extern crate` items (`RustSource.UseLeaves`: `use a::{b, c}` is `use a::b;` and `use a::c;`, attributes and visibility kept)
  whose name the base's module does not already import is added at the module's end (`CargoNewTestSet.AddedUses`), `pub use`
  re-exports included. A file the base lacks (added) that holds new tests is written with its inner attributes, its imports and
  its `#[cfg(test)]` items alone (an empty `fn main` kept for a binary) and its `mod` declaration added to the base's parent
  module file, so a test of the file's new code is compiled on the base and fails there in its own function, while one that
  only reaches what the base has compiles and gets its real result; a manifest's `dev-dependencies` tables are the head's (a
  new package's manifest is applied whole, its new crate roots as any new file). Unisolable (`unsupported`): a changed `.rs`
  file the reader cannot read; a new test outside `src/` and `tests/`, in no package, or in a module shared by integration
  tests (`tests/common/mod.rs`); a custom target layout (`[[test]]`, `autotests`/`autobins`/`autolib`, a non-default `path`); a
  test function, or tests inside a macro, outside every `#[cfg(test)]` module that the base does not have unchanged (also at
  the top of a new file, or in a module gated by `cfg(any(test, …))`: the items it uses beside it cannot be told from
  production code — "move it into a #[cfg(test)] module"); a `#[cfg(test)]` item that is not a module (a `use`, an `impl`, a
  helper) outside every `#[cfg(test)]` module, that the base lacks; two items of one kind and name in a module at the base or
  the head that the graft would match (cfg alternates: two `mod tests`, two `#[test] fn t`; never two edits of one place —
  `Graft` also refuses overlapping edits); a renamed production file that holds new tests (its production code is the base's
  under another name); dotted `dev-dependencies` keys; a new file whose `mod` declaration is not at the top of its parent's
  file. Runs, from the worktree root per package with a new test, all through `SandboxTestRunner` like xUnit's (as `_factory`,
  no variables): `cargo --version` (`TestPhase.Toolchain`: failing, the run is `ToolchainMissing` → `error` naming cargo, never
  a pass — cargo is needed only where a Rust check runs, so a .NET-only factory needs none; `setup-worker-user.sh` reports
  `cargo`/`rustc` as an optional check; they must be on the launch helper's fixed PATH, `/opt/homebrew/bin` or
  `/usr/local/bin`: the helper did not change; the same PATH serves an implementing worker's own `cargo build/check/test/fetch`,
  `ClaudeWorker.CargoTools`, sc-25877), `cargo fetch` (the restore, the only step that may use the network, as `dotnet
  restore` is; `cargo` reaches the lint only through `TestStep.Program`, the same known local program), `cargo test --offline
  --no-run --message-format=json` for the targets with new tests (the build: cargo's JSON diagnostics, stable), then one `cargo
  test --offline … --no-fail-fast -- --exact <path>` per new test (`--lib`, `--bins` or `--test <name>`; a parameterized one by
  its `path::` prefix); `--offline`: the build and the tests resolve and download nothing. cargo reports on standard output, so
  such a step's output is kept by the runner in memory with its exit code (`TestStep.CaptureOutput`, `ResultFile.ExitCode`;
  nothing the run writes is read as a result). Results: libtest's JSON is nightly-only, so the stable lines are parsed
  (`running N tests`, `test <name> ... ok|FAILED|ignored`, `test result: …`) and read only when they have libtest's shape and
  agree with the exit code — per test binary a `running N tests` line, then N test lines, then a summary counting them; no test
  line outside such a block; no test with two results; exit code 0 with no failure or non-zero with one; anything else (no
  summary: a crashed or custom harness; a mismatch: a test that printed) is an unreadable report, `error`. `ignored` is skipped;
  a run of 0 tests gives no case (`not-run`: `error` on the base, not passing on the head). A base that does not build: each
  `compiler-message` error (file placed from rustc's workspace-relative name and the package's manifest path) is put in the
  innermost item at its position; only what the overlay applied counts (a whole test file; in a grafted file a `#[cfg(test)]`
  item, or an import the overlay added): in a new test's own function → `not-built`; another item of the test code (a helper)
  is left out of the retry, and of a `use` only the single imports the error lies in (the others stay, so `use words::{words,
  trim}` with a new `trim` still imports `words`); an error in a module's own header or between its items is unremovable (the
  tests stay `unproven`); anywhere else — the base's production code or its own imports, a file not applied (e.g. another
  module's test the base cannot build), no file (manifest, dependency, linker, no rustc), an ambiguous name (E0659), or no
  error named — is unattributable, `error`. Retries: `INewTestStrategy.BaseRetries` is 1 for xUnit and 3 for cargo, since rustc
  reports in stages (an import that does not resolve hides the errors of the code using its name, which may hide type
  errors): each retry (`gate-sc-<id>-base-retry`, `-base-retry-2`, `-base-retry-3`) leaves out what the run before could not
  compile, the last one that ran judges the remaining tests, and a base that still does not build after them leaves those
  `unproven` (rejected). Residuals, accepted: (a) the test process writes all of its output and picks its exit code, so a test
  that prints a complete libtest report and calls `std::process::exit(101)` is read as failed on the base — reviewed code that
  lies about itself, as a test that asserts on the source tree would be; (b) `~_factory/.cargo` (a global `config.toml` with
  `runner`, `rustc-wrapper` or `[env]`, and the registry cache `cargo fetch` fills) persists across items and is writable by
  every worker session, so an implementer can influence later gate runs, on both commits — the analogue of `_factory`'s NuGet
  cache and config for dotnet; (c) production code is the base's by file: code the PR moves into a new file, a new non-test
  helper, macro or dependency, and a re-export in another package are things the base lacks, so a test that needs one is
  `not-built` even if it otherwise exercises old code (as a test calling a new helper outside the test projects is for xUnit);
  (d) by design, not a gap: a file-level import is production code, so an import the PR rebinds (the same name from another
  path) keeps the base's binding in a grafted file — what the real base has; a test that only passes with the new binding
  fails on the base, as it should.
  Upgrading from Phase 1: Review is now a handled state, so the first `factory work` picks up every item parked at Review:
  an open PR is reviewed by the panel like any other (Phase 1's missing `implementer-model` no longer matters, sc-25379),
  and one the owner merged or closed escalates once with a story comment ("merged outside the factory"). An item whose
  head already has a verdict recorded under an earlier panel rule (pinned reviewer models, before sc-25626: no served
  `class`) is reviewed again once by the current panel (`MergeGate.Superseded`: its calls break `ReviewModels.Problems`
  and it is the only verdict on that head — at Review it is not reused; at MergeGate the gate answers ReviewHead when those
  are its only reasons), then merges as usual; the current panel's own verdict is a second one on the head, so if its calls
  still break the rule the gate escalates instead of reviewing again. The fix loop and CI self-heal never carry such a
  role's review (`FixLoop.MustReviewAgain`). See docs/acceptance.md.
- Processes migrate the ledger through `LedgerMigrations.MigrateAsync` (advisory-locked: EF alone lets two concurrent
  migrators apply the same migration) before reading it; tests migrate their temp database before starting a host.
- Tests: xunit.v3 on Microsoft.Testing.Platform (`global.json` opts in).
