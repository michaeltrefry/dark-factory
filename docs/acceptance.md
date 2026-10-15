# Phase 1 acceptance runbook (epic sc-25171, AT1–AT8)

How to run each epic acceptance test, what is automated and what the owner does by hand. Everything live is
opt-in: the tests skip, naming the missing prerequisite, unless their gate variable is set.

## Safety first (read before any live run)

- **The `_factory` user is single-tenant** — and so is each further sandbox user of a machine set up with worker slots
  (`_factory2`, …; `Worker:Slots`, sc-25989). Every exit of a sandbox launch helper kills *every* process of its user.
  Do not use an interactive `_factory` session (e.g. its Claude login) while any live test below runs.
- **Stop your own `factory work` / `factory run` first.** The `factory work` tests (AT2–AT5) hold the configured
  work root's sandbox lock (`<Factory:WorkRoot>/.factory-run.lock`, and `slot-locks/.factory-run-<n>.lock` of every further slot) for
  their whole run and skip if a real run's worker
  phase holds one at that moment, so they never share a sandbox user with one (a real run started meanwhile waits at its
  worker phases, sc-25988). They run against a **throwaway ledger database**
  (`df_e2e_at<N>_<guid>` on the configured Postgres, dropped afterwards) and their **own work root**
  (`<Factory:WorkRoot>/e2e/<db name>`), so they never read or change your real ledger, controls or worktrees.
  Clones left under `<Factory:WorkRoot>/e2e/` may be deleted by hand afterwards.
- **The watch scope must hold only the test story.** The test host claims *every* To Do story in
  `FACTORY_E2E_WATCH_TEAM` / `FACTORY_E2E_WATCH_EPIC`. Use a dedicated test epic (`FACTORY_E2E_WATCH_EPIC`) holding
  just the story under test, rather than a whole team.
- **Each test needs its own fresh To Do story** (a claimed story is In Progress afterwards). Stories target the
  sandbox repo (`Factory:DefaultRepo`, or a `Repo: owner/name` line) and should be small bugs; AT3 and AT4 need a
  story that keeps the worker busy for a few minutes (e.g. "add tests for X and fix what they find").
- **The tests spend real router tokens** and write real Shortcut comments/links and GitHub branches/PRs on the
  sandbox repo. Nothing merges.
- The dashboard login of the throwaway hosts uses a test password whose hash is injected into an in-memory overlay of
  the secret store; your keychain's `dashboard-password-hash` is never read or written.
- AT3 SIGKILLs only the `factory work` child process it started, by its own pid (never a process tree, never anything
  else). The orphaned worker is then stopped by the product's own orphan handling (sandboxed: a helper run whose exit
  kills every `_factory` process — see the first bullet).

Common prerequisites: `docker compose up -d` (ledger Postgres on 5434), the router on `Router:BaseUrl`
(default `http://127.0.0.1:8790`: simple-router, michaeltrefry/simple-router), keychain entries for the router key, Shortcut token and GitHub App
(`factory github-app setup`), `sudo scripts/setup-worker-user.sh` done once (or `Worker__RunAs=none` to run workers
as yourself, development only), and the model auth (`Worker:Auth=router-key`, the only mode: `claude-login` was retired, sc-26197): your plans enrolled on
the router for the router key (`simple-router login claude`, `simple-router login codex`, run in the simple-router
repository as `dotnet run --project src/SimpleRouter -- login claude|codex`; `factory run`/`work` refuse to start
without one) and `_factory`'s own login removed (`sudo scripts/setup-worker-user.sh --remove-worker-login`).

Run one test at a time with a class filter, e.g.:

```sh
FACTORY_E2E=1 FACTORY_E2E_WATCH_EPIC=<test epic id> FACTORY_E2E_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*FactoryWorkTests" \
  --filter-method "*Factory_work_takes_a_ready_story*"
```

## AT1 — the test suite and CI are green

Automated, not live.

1. `dotnet build` (0 warnings) and `dotnet test` from the repo root (CI runs the same). `LiveWorkerSandboxTests` skip
   without `FACTORY_SANDBOX_LIVE=1`; the acceptance tests skip without `FACTORY_E2E=1`.
2. Owner: confirm the dark-factory PR's CI (`.github/workflows/ci.yml`) and the sandbox repo's CI are green on GitHub.

## AT2 — end to end through `factory work`

Automated: `FactoryWorkTests.Factory_work_takes_a_ready_story_to_review_with_links_ledger_replay_and_cost`.

| Variable | Value |
| --- | --- |
| `FACTORY_E2E` | `1` |
| `FACTORY_E2E_WATCH_TEAM` and/or `FACTORY_E2E_WATCH_EPIC` | the test host's watch scope |
| `FACTORY_E2E_STORY` | `sc-<id>`: a To Do bug story in that scope targeting the sandbox repo |

What it does: starts `FactoryHost.BuildWork` (dashboard, session hub, intake loop) in process on a free port against a
throwaway ledger, waits (≤ 45 min) for the item to reach Review, then asserts: the story is **In Progress** with the
**PR and branch external links**; the ledger transitions are exactly **Intake → Implement → Review**; exactly one PR
from `factory/sc-<id>`; after a **real login** (login page, antiforgery token, form post) the dashboard's
`/sessions/{id}` page names the item and the session hub **replays every stored event** of the session in order; the
**pipeline shows the item's recorded cost** (≥ $0, shown even when $0.00); and AT7 for the run's sessions. `WalkingSkeletonTests` additionally covers the
`factory run --ignore-scope` path (it also uses `FACTORY_E2E_STORY`; give it a different story).

## AT3 — crash: kill -9 mid-Implement

Automated: `CrashRestartTests.Factory_work_killed_mid_implement_resumes_the_same_session_and_opens_exactly_one_pr`.

Variables: `FACTORY_E2E=1`, the watch scope as in AT2, `FACTORY_E2E_CRASH_STORY=sc-<id>` (a To Do story in scope
that keeps the worker busy a few minutes).

What it does: runs the built CLI `dotnet DarkFactory.Orchestrator.dll work` as a child process (throwaway ledger and
work root via env, test dashboard password hash via `Dashboard__PasswordHash`), waits for the `session` checkpoint,
SIGKILLs **that child's own pid only**, starts a second child, and waits for Review. Asserts: the killed run's worker
was stopped as an orphan (`orphan-killed`) or is already gone (checked read-only with `ps -p`); every `session`
checkpoint and the Review row carry the **same Claude session id**, and `worker_sessions` holds only that one; and
**exactly one PR** exists for `factory/sc-<id>`. The unit-level version (`CrashResumeTests`, fake `claude`) runs in
every `dotnet test`.

## AT4 — controls on a live run

Automated: `FactoryWorkTests.Pause_continue_and_stop_at_item_and_factory_scope_on_a_live_run`.

Variables: `FACTORY_E2E=1`, the watch scope, `FACTORY_E2E_CONTROL_STORY=sc-<id>` (To Do, **inside** the scope, a few
minutes of work) and `FACTORY_E2E_CONTROL_STORY_2=sc-<id>` (To Do, **outside** the scope, a few minutes of work).

What it does, through the same code as `factory pause|continue|stop` (`FactoryRunner.ControlAsync`):

1. Host running, story 1 claimed and its worker running with a session.
2. **Item Pause** → the run records Paused `user-paused` (worker stopped at a tool boundary, ≤ the pause grace);
   **item Continue** → the intake loop resumes it and a worker runs again with the **same Claude session**.
3. **Factory Pause** → Paused `user-paused`; **factory Continue** → resumed, same session.
4. **Item Stop** → Cancelled, a `stop-reported` checkpoint, story back in **Backlog**.
5. Host stopped. Story 2 run like `factory run --ignore-scope`; once its worker runs, **factory Stop** → Cancelled and
   Backlog.

If a worker finishes before a control lands the test fails saying so: pick a longer story. The dashboard's buttons post
to the same `ControlActions` (covered by `DashboardTests`); optionally, while the test runs, watch the throwaway host's
pipeline page (address printed in the log) to see the states change.

## AT5 — usage: nothing dispatches before T, work resumes at T

Automated: `FactoryWorkTests.Nothing_dispatches_while_the_router_reports_plans_exhausted_and_work_starts_at_the_reset`.

Variables: `FACTORY_E2E=1`, the watch scope, `FACTORY_E2E_USAGE_STORY=sc-<id>` (To Do bug story in scope).

What it does: starts `StubRouter` (an in-test Kestrel on 127.0.0.1) that answers `GET /v1/subscriptions/usage` with
`{all_exhausted: true, resumes_at: T, known_credentials: 1}` until T = start + 2 minutes (and not exhausted after T),
and proxies every other path (model calls, streamed; session costs) to the real router. The host (and its workers) use
the stub as `Router:BaseUrl`, with a 600 s poll so only the pause's own wake-up can explain a claim at T. Asserts: the
usage pause row has `ResumeAt = T`; until T − 10 s the throwaway ledger has no row for the story and it is still To Do;
the Intake row is recorded at or after T and within 60 s of it; the worker is dispatched; the run reaches Review; AT7
for its sessions.

## AT6 — safety

| Check | Test | Gate |
| --- | --- | --- |
| Worker token pushes `factory/*` only on its own repo; push to `main` and to another repo rejected | `AppTokenPushTests.Worker_token_pushes_factory_branches_only_on_its_own_repo` | `FACTORY_E2E=1` (`FACTORY_E2E_OTHER_REPO` optional) |
| Worker env is exactly the allowlist (`ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`, `ANTHROPIC_CUSTOM_HEADERS`, `DOTNET_CLI_USE_MSBUILD_SERVER`, `HOME`, `MSBUILDDISABLENODEREUSE`, `PATH`, `TMPDIR` — the sandbox user's own temporary directory, sc-25992); `ANTHROPIC_CUSTOM_HEADERS` is two lines, the router key and `x-weave-model-class` (sc-25659) | `LiveWorkerSandboxTests.Live_worker_environment_is_exactly_the_allowlist` | `FACTORY_SANDBOX_LIVE=1` |
| `_factory` holds no Claude credential (no `~/.claude/.credentials.json`, no `Claude Code-credentials` keychain item) | `LiveWorkerSandboxTests.Worker_user_holds_no_claude_credential` | `FACTORY_SANDBOX_LIVE=1` |
| `router-key`: `factory run`/`work` refuse to start without an enrolled plan | `RouterEnrollmentCheckTests` (unit, fake router) | none |
| `~michael`, `.ssh`, `.config/gh`, login keychain unreachable | `LiveWorkerSandboxTests.Worker_is_denied_the_owners_home_ssh_keys_gh_config_and_login_keychain`, `Worker_cannot_read_the_owners_keychain_items` | `FACTORY_SANDBOX_LIVE=1` |

The env probe runs `/usr/bin/env` through the installed helper exactly as `ClaudeWorker` launches a sandboxed worker
(`WorkerSandbox.Start` with `ClaudeWorker.BuildRouterVariables` for the configured router URL and auth mode, a probe
router key, and `GH_TOKEN` set in the test process to show nothing of the owner's leaks). Every probe runs once per configured
worker slot (`Worker:Slots`: each slot's own user and installed helper, `_factory`, `_factory2`, …; sc-25992), a failure naming
its slot; slot 1 not ready (the machine not set up) skips them, a further configured slot not ready fails. The env probe also
checks that each slot's `TMPDIR` is under `/var/folders/`, owned by that slot's own uid (run as it: `stat -f %u "$TMPDIR"` equals
`id -u`, and `id -un` is the slot's user), not the owner's, and that no two slots share a `TMPDIR` or a `HOME`. The
`FACTORY_SANDBOX_LIVE` tests kill every process of every configured sandbox user: run them only when no sandbox user's session
is in use and `factory work` is stopped. One slot (the default):

```sh
FACTORY_SANDBOX_LIVE=1 dotnet test --project tests/DarkFactory.Orchestrator.Tests --filter-class "*LiveWorkerSandboxTests"
```

Two slots (after `sudo scripts/setup-worker-user.sh --slots 2`):

```sh
FACTORY_SANDBOX_LIVE=1 Worker__Slots=2 dotnet test --project tests/DarkFactory.Orchestrator.Tests --filter-class "*LiveWorkerSandboxTests"
```

## AT7 — the router accounts for every model call

Automated, three ways: AT2 and AT5 end by checking every `worker_sessions` row of their run; standalone,
`RouterAccountingTests.Router_reports_requests_and_cost_for_every_worker_session_since_the_given_time` checks any
ledger (`FACTORY_E2E_LEDGER`, default the configured one; read-only) for sessions started since `FACTORY_E2E_SINCE`
(ISO 8601). Each session must be recorded: `GET /v1/sessions/{id}/cost` answers 200 with `request_count > 0`, and its
cost is ≥ 0. A cost of 0 is valid: the router may serve turns (or a whole session) on a local model priced at $0, which
still counts toward `request_count`. A 404 or `request_count == 0` means not recorded (yet).

That proves every *session* is costed, not that no call bypassed a session. Owner, by hand, for the AT2 window: in the
router's request records (simple-router's recent requests, dashboard `http://127.0.0.1:8790/ui`, or its store), count the model requests in the window and compare with the sum of the sessions' `request_count`; any request
with no Claude Code session id is a call the factory made outside a worker session and fails AT7. The exact log field
or query that carries the session id is the router's (simple-router, michaeltrefry/simple-router) and is not pinned here — check
it there before relying on a count.

## AT8 — tracker readback

By hand (the terminal story, sc-25182): read every story of epic sc-25171 and the epic itself back from Shortcut and
confirm their final states (stories Done once merged and validated; the epic done). Tracker writes are verified by
reading them back, since the Shortcut tools report success even for ignored parameters.

# Phase 2 acceptance runbook (review → gate → merge)

## P2-AT1/P2-AT2 — the walking skeleton merges on the sandbox (sc-25378)

Automated: `ReviewGateTests` (tests/DarkFactory.AcceptanceTests), live, skipped unless `FACTORY_E2E=1`. Each runs
`factory run` (production wiring, `--ignore-scope`, a throwaway ledger) on its own To Do bug story whose fix lands in the
sandbox, and the gate **really merges** the PR into the sandbox's main.

Owner set-up, once (the test skips naming whichever is missing):

1. `factory github-app setup --gate`, then install the gate App on the sandbox repo.
2. `factory github-repo protect michaeltrefry/dark-factory-sandbox` again, so the "only admins write outside factory/**"
   ruleset lists the gate App as a `pull_request`-mode bypass actor (it may merge PRs, never push). Re-run it once more
   after sc-25391's ruleset fix merged (a `protect` from before it left `require_extra_approval_for_unattributed_changes`
   at GitHub's default `true` on the main ruleset, which blocks every PR of unsigned commits — sandbox PR #6 among
   them); it must end with three `verified ruleset` lines, and then sandbox PR #6 is no longer blocked.
3. A committed `factory/gate.yaml` on the sandbox's main in the version-2 format (sc-25381: path tiers and a risk
   threshold; michaeltrefry/dark-factory-sandbox#6). A version-1 policy is rejected: every review escalates. The gate
   stories must change only normal or free paths (e.g. `src/Sandbox/`, `tests/`), small enough for the risk threshold —
   a sealed or protected path escalates instead of merging. Since sc-25382 a normal-tier change must also add an xUnit test
   that fails on the base and passes on the head (`new-tests-fail-on-base`, run sandboxed as `_factory`, so `sudo
   scripts/setup-worker-user.sh` must be in place), and the policy must list that check in `normal` and `protected`.
4. **CodeGraph, since Phase 3 (sc-25709):** every normal or protected change needs `risk-threshold`, which reads CodeGraph's blast
   radius for the head and hotspots for the default branch, so a live merge also needs (a) the CodeGraph token in the keychain
   (account `codegraph-token`, or `CodeGraph:Token` / env `FACTORY_CODEGRAPH_TOKEN`; without one `factory run`/`work` warn at
   start-up and the gate fails `risk-threshold` at once), (b) `michaeltrefry/dark-factory-sandbox` indexed in the hosted CodeGraph
   (its project's repository URL exactly `https://github.com/michaeltrefry/dark-factory-sandbox`), and (c) CodeGraph's head overlays
   (C4/C6, michaeltrefry/CodeGraph PRs #71/#75) and risk tools (C7, PR #76: `analyze_impact_file`, `file_hotspot`) deployed and the
   token entitled to them. Missing any of these, the gate waits on `risk-threshold` like pending CI and escalates at
   `Gate:CiTimeoutMinutes` instead of merging — so `ReviewGateTests`, `IssueTriageTests`' build test, the P2 epic AT2 run and the
   Phase 1 AT1–AT5 runs (which go through the gate) all need them; `GateNegativeTests` and `FreezeAndStuckTests` escalate before
   `risk-threshold` is read and do not.

No reviewer model is set anywhere (owner decision 2026-10-09, epic E8, sc-25626): every review and second opinion names
the router's `high` model class (`x-weave-model-class: high`, never `x-weave-force-model`, which simple-router ignores) and
the router picks the model, so the router must have a servable `high` tier (simple-router answers 429 `rate_limit_error` whose
message starts `model_class_unavailable:`, with `Retry-After`, when no model of the tier can serve now; that pauses the factory
for usage). simple-router takes the tier from the header and never from the body model (`default`). A call counts only when the router's `X-Weave-Model-Class` response
header says `high`. The retired
`Review:*Models` settings are refused: with one set, these tests, the seeded review (`ReviewSeedTests`) and the Phase 1
AT1–AT5 runs skip naming it, and `factory run`/`factory work` print the reason and exit 2.

| Test | Variables | Proves |
| --- | --- | --- |
| `Gate_merges_a_green_pr_a_high_class_panel_passed_and_the_ledger_records_the_merge_commit` | `FACTORY_E2E_GATE_STORY=sc-<id>` | Intake → … → Review → CI → MergeGate → Merge → Watch; one pass verdict from the review panel (sc-25379: correctness and spec conformance, plus security when a touched path's tier requires it), every review and second opinion served on the router's `high` model class (whichever model it picked, even the implementer's), each panel call accounted by the router under its own session, which the ledger named with the role's prompt file hash; the PR merged at the reviewed head; the Merge row holds GitHub's merge commit; the story is Done |
| `A_push_after_the_verdict_blocks_the_merge_until_the_new_head_is_reviewed_again` | `FACTORY_E2E_GATE_PUSH_STORY=sc-<id>` | right after the first review the test pushes one commit to the PR branch (as the workers' App) that keeps the reviewed tree (`SandboxRepo.PushSameTreeAsync`: the head moves, the change stays exactly the story's, so the panel has nothing new to block — a probe file once got blocked as out of scope); checked as invariants (`PushAfterVerdict.Problems`, in-process `PushAfterVerdictTests`), not one transition list, since a fix round after the pushed head's review is legitimate: a passing verdict on the pre-push head; the next Review comes straight from CI or MergeGate (no Fixing/CIHealing between) with detail `head moved to <pushed sha>`; a verdict on the pushed commit; no `gate-passed` on the pre-push head; every `gate-passed` head had a passing verdict before it; transitions end MergeGate → Merge → Watch; the merged PR's head is the last `gate-passed` head (which equals the pushed commit unless a fix round ran). A run that did not take the push-after-verdict path (the first verdict failed, no verdict on the pre-push head, or a fix/CI-healing round before the next Review) fails as `INCONCLUSIVE`: rerun it on a fresh story |

```sh
FACTORY_E2E=1 FACTORY_E2E_GATE_STORY=sc-<id> FACTORY_E2E_GATE_PUSH_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*ReviewGateTests"
```

The seeded review (sc-25379) needs only the router and the router key: no GitHub, Shortcut, ledger or gate App. It sends
the fixture diffs `tests/DarkFactory.Orchestrator.Tests/Fixtures/review/*.diff` through the production `RouterReviewer`
on the router's `high` model class (the router picks the model; each call must come back with `X-Weave-Model-Class: high`).
Real models answer, so a failure names what they reported.

| Test | Variables | Proves |
| --- | --- | --- |
| `ReviewSeedTests.The_spec_conformance_prompt_gets_a_real_model_to_flag_an_unused_config_flag_and_a_second_opinion_confirms_it` | `FACTORY_E2E_REVIEW_SEED=1` | the spec-conformance prompt (`factory/prompts/spec-conformance.md`) gets a real model on the high class to report a blocking finding naming `IgnoreBlankInput` on `unused-config-flag.diff` (a flag nothing reads), and the confirm prompt, in a second call on the high class (any model, possibly the same one), answers `confirmed` |
| `ReviewSeedTests.The_same_flag_with_a_consumer_gets_no_blocking_spec_conformance_finding` | `FACTORY_E2E_REVIEW_SEED=1` | the same flag with a consumer (`consumed-config-flag.diff`: the flag on by default, read by `Count`, with tests of both settings and a row in the README's configuration table), sent with a story that asks for that setting (`ReviewSeedTests.ConsumedStory`; the unused-flag test keeps its own story), gets no blocking spec-conformance finding. The fixture's consistency is checked in every `dotnet test`: `ReviewFixtureTests` applies it to the WordCount base with `git apply`, `ReviewSeedStoryTests` matches it to the story |

```sh
FACTORY_E2E=1 FACTORY_E2E_REVIEW_SEED=1 \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*ReviewSeedTests"
```

Since sc-25378 every `factory run` / `factory work` run goes on past Review, so the Phase 1 tests above (AT1–AT5) also
need the gate App, and their stories' PRs get merged when they pass the gate.

## P2-AT-issues — GitHub issue triage, routing by author, collaborator approval (sc-25385)

Automated: `IssueTriageTests` (tests/DarkFactory.AcceptanceTests), live, skipped unless `FACTORY_E2E=1` and
`FACTORY_E2E_ISSUES=1`. They open real issues on the sandbox repo (`Factory:DefaultRepo`, watched through
`GitHub:Watch:Repos`), run the production issue intake (`FactoryRunner.PollIssuesAsync`: the triage is a real sandboxed
worker through the router) against a throwaway ledger, and close every issue they opened. GitHub's `issues?since=` listing
can show a just-opened issue (or a just-posted `Approved` comment) late, so each step polls the intake again every 5 s until
it has acted on it, and fails naming what it never saw after 2 min (`PollUntil`).

These tests need the worker sandbox: issue triage refuses `Worker__RunAs=none` (the triage reads untrusted issue text).

Owner set-up, once: grant the factory's (workers') App the **Issues: Read and write** repository permission
(`src/DarkFactory.Orchestrator/GitHub/app-manifest.json` lists it for new registrations; an existing App needs it added in
its GitHub settings and the installation's update accepted), plus everything P2-AT1 needs (the gate merges the first test's PR).

| Test | Variables | Proves |
| --- | --- | --- |
| `A_collaborators_issue_with_a_confident_fix_is_built_and_its_merged_pr_closes_the_issue` | `GH_TOKEN` (the owner) | the owner's issue is triaged, routed `build` and released at once; `factory run gh-<key>` takes it through the gate; the PR body says `Closes owner/name#N`; the merged PR closed the issue |
| `An_outsiders_issue_waits_for_a_collaborators_approval` | `GH_TOKEN`, `FACTORY_E2E_OUTSIDER_TOKEN` (an account without write access) | the outsider's issue gets the triage comment and `awaiting-approval` and is not built; GitHub names the factory's App (`performed_via_github_app.id`) on that comment, posted by a Bot account (the unit fixtures are written, not recorded); the outsider's own `Approved` is ignored (recorded); the owner's `Approved` releases it |

```sh
FACTORY_E2E=1 FACTORY_E2E_ISSUES=1 GH_TOKEN=$(gh auth token) FACTORY_E2E_OUTSIDER_TOKEN=<token> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*IssueTriageTests"
```

## Phase 2 epic acceptance tests (epic sc-25377: AT2, AT4, AT5, AT6; sc-25391)

The terminal story runs these once, live, against `michaeltrefry/dark-factory-sandbox`. Each is opt-in (`FACTORY_E2E=1` plus its
own variables) and skips naming whatever is missing; each runs the production wiring (`factory run`, `FactoryRunner.RunAsync`, or
the issue intake `FactoryRunner.PollIssuesAsync`) against a **throwaway ledger** and its own work root, like the tests above. Every
`*_STORY` must be a **fresh To Do bug story** whose fix lands in **normal** code of the sandbox (e.g. `src/Sandbox/`, not `tests/`,
`docs/`, a sealed or a protected path), small enough for the risk threshold — the P2-AT1 kind; give each test its own story.
Prerequisites: everything P2-AT1 needs (gate App installed, rulesets re-applied, a version-2 `factory/gate.yaml` on the sandbox's
main, `sudo scripts/setup-worker-user.sh`, and — for any run that is meant to merge — the CodeGraph token, the sandbox indexed in
CodeGraph and C7 deployed, P2-AT1 step 4).

```sh
FACTORY_E2E=1 FACTORY_E2E_GATE_SEALED_STORY=sc-<id> FACTORY_E2E_GATE_POLICY_STORY=sc-<id> GH_TOKEN=$(gh auth token) \
  FACTORY_E2E_GATE_PASSING_TEST_STORY=sc-<id> FACTORY_E2E_GATE_ROUNDS_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*GateNegativeTests"
FACTORY_E2E=1 FACTORY_E2E_FREEZE_STORY=sc-<id> FACTORY_E2E_STUCK_STORY=sc-<id> FACTORY_E2E_SILENT_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*FreezeAndStuckTests"
```

### AT2 — a To Do sandbox bug story goes end to end with no human touch

Automated: `ReviewGateTests.Gate_merges_a_green_pr_a_high_class_panel_passed_and_the_ledger_records_the_merge_commit` (and the
same checks at the end of `A_push_after_the_verdict_blocks_the_merge_until_the_new_head_is_reviewed_again`), variables as in P2-AT1
above. On top of P2-AT1's assertions (states, the high-class panel through the router, the merge commit, the story Done), the run's
ledger is checked row by row (`ReviewGateTests.AssertTypedOutcomesQueueAndReportsAsync`):

- **a typed outcome per step:** every row's `Outcome` equals `StepOutcomes.Of(<the item's state before it>, State, Step, Detail)`;
- **the merge queue:** a `queued` row, then `queue-turn`, then `gate-passed`, then the Merge transition;
- **the PR body matches the ledger:** the merged PR's description equals `LedgerReport.PullRequestBody` of the rows written before
  its rewrite (`pr-report` `merge`) and the worker sessions' recorded costs;
- **the closeout matches the ledger:** a comment on the story equals `LedgerReport.MergedCloseout` of the rows written before the
  `closeout` `posted` row (attributed `[author: dark-factory]`).

The costs are read when the test checks: if the session recorder wrote a cost after the merge, the body check fails showing both
texts (rerun the comparison by hand from the throwaway ledger before it is dropped, or rerun the test).

### Worker model classes (E8, sc-25659)

Every worker session names a router model class and pins no model. Prerequisite: the launch helper is helper_version 3 (it joins
the two `ANTHROPIC_CUSTOM_HEADERS` lines); a helper installed before sc-25659 fails start-up as stale, so re-run
`sudo scripts/setup-worker-user.sh` once (stop `factory work` first: it kills every `_factory` process). The router must know the
classes (`x-weave-model-class`: simple-router's tiers `max`, `high`, `mid`, `low`) and have a servable model in `mid` and `low`.

Unit-tested (no gate): the class per role and the simple/complex rule (`WorkerModelClassTests`; conflict fix rounds in
`MergeQueueTests.A_branch_that_conflicts_with_main_goes_to_the_fix_loop_instead_of_merging`, triage in `TriageWorktreeTests` and
`IssueIntakeTests`), the headers and the unchanged allowlist (`WorkerModelClassTests`, `WorkerSandboxTests`), and a
`model_class_unavailable` refusal pausing for usage instead of escalating while the same words in a non-error result still
escalate (`UsagePauseTests`, `IssueIntakeTests`). The live check rides on the AT2 run above, by hand, from its throwaway ledger
before it is dropped:

- every `worker_sessions` row's Claude session has a `worker-model-class` checkpoint, `mid` for the implementer of an unestimated
  bug story (`low` if the story carries the `simple` label or 1–2 points), the same class for its review fix rounds, `mid` for a CI
  fix round;
- the router served each of those sessions' requests inside its class: its answers carry `X-Weave-Model-Class` naming that class,
  and `GET /admin/v1/models` gives each served model's `class` (which log field or telemetry query shows the served model per session
  is the router's and is not pinned here — check it there). Claude Code's own small utility calls (titles, summaries) carry the same
  headers and so the same class: expected, not a failure.

A run whose router has no servable model in the class shows Paused `usage-paused` with a `usage-pause` checkpoint
`worker-model-class-unavailable; resumes at …`, never Escalated.

### AT4 — gate negatives on seeded PRs

Automated: `GateNegativeTests` (`tests/DarkFactory.AcceptanceTests`). The review panel is replaced by a seeded stand-in
(`SeededReviewer`: every role answered on the high class with no finding, or one confirmed blocking finding), so
the gate's own rule decides deterministically; the real panel is AT2's. The seeding writes go through the gateway's GitHub client:
to the item's own `factory/sc-<id>` branch as the workers' App (it may write only `factory/**`), or to a throwaway
`e2e/corrupt-policy-<guid>` branch as the owner (`GH_TOKEN`). **The sandbox's main is never written.** None of these merges.
**Cleanup (automatic):** each test closes its PR and deletes its `factory/sc-<id>` branch (and the throwaway branch) at the end.
**By hand:** move each story back (it ends In Progress with an escalation comment) or archive it; delete a branch a crashed run
left (`factory/sc-<id>`, `e2e/corrupt-policy-*`).

| Test | Variables | Setup it does | Expected on the ledger |
| --- | --- | --- | --- |
| `A_sealed_path_on_the_pr_head_escalates_instead_of_merging` | `FACTORY_E2E_GATE_SEALED_STORY` | before the first review, commits `factory/prompts/e2e-sealed-probe.md` (sealed on every valid policy: the floor seals `factory/prompts/`) to the PR branch | the head moves, back to Review (`gate_rejected`), reviewed, CI; the last `gate` row is `Blocked …touches sealed path(s), which always escalate…factory/prompts/e2e-sealed-probe.md (sealed)` (`gate_rejected`); transitions end MergeGate → Escalated (`escalated`); no `gate-passed`, no Merge; the PR is not merged |
| `A_corrupt_gate_policy_blocks_the_merge_and_escalates` | `FACTORY_E2E_GATE_POLICY_STORY`, `GH_TOKEN` (the owner, an admin) | branches `e2e/corrupt-policy-<guid>` off main and commits `factory/gate.yaml` = `version: [` there; once the item is reviewed (on the real policy), every policy read of the gate is redirected to that branch (`PolicyFromRef`), so the gate reads a really corrupt file through GitHub | the last `gate` row is `Blocked …factory/gate.yaml is not valid YAML…` (`gate_rejected`); MergeGate → Escalated; nothing merged; the throwaway branch deleted |
| `A_new_test_that_already_passes_on_the_base_is_gate_rejected` | `FACTORY_E2E_GATE_PASSING_TEST_STORY` | before the first review, commits `<the sandbox's first *Tests.csproj directory>/E2eAlreadyPassingTests.cs` (one `[Fact]` asserting `true`) to the PR branch | the last `new-tests` row is `rejected` (`gate_rejected`) naming `DarkFactoryE2e.E2eAlreadyPassingTests.Already_passes_on_the_base` (passed on the base and the head); the last `gate` row is `Blocked …new-tests-fail-on-base (rejected)…`; MergeGate → Escalated |
| `A_fix_round_past_the_cap_escalates_with_the_open_finding` | `FACTORY_E2E_GATE_ROUNDS_STORY` | none: the seeded panel reports one confirmed blocking correctness finding on every head (the real fixer runs each round, so this spends one fixer session per round) | the cap is min(the PR base's `factory/gate.yaml` `risk.max_fix_rounds`, `Lifecycle.MaxFixRounds` = 3) — 2 on the sandbox's policy — read by the test from the base (`GateNegativeTests.ExpectedFixCap`) and equal to the ledger's (`RunPipeline.FixCapOf`, the `fix-cap` row); exactly that many Fixing transitions (`failed`), every verdict `fail`, transitions end Review → Escalated, never MergeGate; the escalation comment says `after <cap> fix rounds` and names the finding |

The push-after-verdict negative is P2-AT2 above (`A_push_after_the_verdict_blocks_the_merge_until_the_new_head_is_reviewed_again`).
The in-process versions, which run in every `dotnet test`: `GatePipelineTests.A_change_touching_a_sealed_path_is_reviewed_but_escalated_by_the_gate_not_merged`,
`A_missing_or_invalid_policy_means_no_merge_and_an_escalation`, `A_policy_that_breaks_after_the_review_still_blocks_the_merge_and_escalates`,
`NewTestsTests.A_new_test_that_already_passes_on_the_base_is_rejected_with_the_test_named`,
`FixLoopTests.A_fourth_round_escalates_with_the_open_findings_attached`.

### P4-AT4 — the new-tests check on a Rust pull request (sc-25875)

Two automated tests in `GateNegativeTests`, each with `FACTORY_E2E=1` and its own To Do bug story like AT4's (same harness and
seeding as the AT4 negatives above):

- the rejection — `P4_AT4_a_rust_test_that_already_passes_on_the_base_is_gate_rejected_and_one_calling_new_code_does_not_build_there`
  (`FACTORY_E2E_P4_CARGO_STORY`): nothing merges and the sandbox's main is not written;
- the merge (sc-25877) — `P4_AT4_a_rust_test_that_fails_on_the_base_and_passes_on_the_head_merges`
  (`FACTORY_E2E_P4_CARGO_MERGE_STORY`): a Rust pull request whose new test fails on the base and passes on the head goes through
  the gate and **merges**. Unlike every other test of the class it writes the sandbox's main: **the sandbox then holds the
  crate**, one directory per run under `e2e-rust-merged/` (`e2e-rust-merged/p<8 hex>/Cargo.toml` and `src/lib.rs`, each marked
  `[dark-factory e2e] … Safe to delete`). They are inert — the sandbox's own CI and .NET build do not read them — and may be
  deleted by an owner's pull request whenever; a new directory per run is what keeps the package new on every base, so the test
  can be re-run. Besides cargo it needs everything P2-AT1 needs for a merge (the gate App, a `factory/gate.yaml` on main, the
  CodeGraph token for `risk-threshold`), and the story's own fix must pass the gate like P2-AT1's (its xUnit test failing on the
  base): the two stacks are judged by their two strategies in one `new-tests` result.

**Prerequisite (once, the owner):** cargo and rustc for the worker user, on the launch helper's fixed PATH — `/opt/homebrew/bin`
or `/usr/local/bin` (`brew install rust`; or rustup installed as `_factory`, with its `cargo` and `rustc` linked into
`/usr/local/bin`: `~_factory/.cargo/bin` itself is not on that PATH). `sudo scripts/setup-worker-user.sh` reports both
(`ok    cargo --version: …`, or `none  cargo --version … not available to _factory`); the helper itself did not change. The first
run also needs the network for `cargo fetch` (the seeded package has no dependency, so nothing is downloaded). Both tests need
this: **cargo and rustc must be on the sandbox user's fixed PATH**, or the check is an `error` and neither a rejection nor a
merge is decided. The same PATH is what an implementing worker's `cargo build`/`cargo test` use (sc-25877: workers may run
`cargo build`, `check`, `test` and `fetch`).

| Setup it does | Expected on the ledger |
| --- | --- |
| before the first review, commits to the PR branch `e2e-rust/Cargo.toml` (package `df-e2e-rust`) and `e2e-rust/src/lib.rs`: `pub fn double`, and an inline `#[cfg(test)] mod tests` with `doubles` (calls `double`) and `already_passes_on_the_base` (asserts `1 + 1 == 2`) | the last `new-tests` row is `rejected` (`gate_rejected`), strategy `rust-cargo`, `base_run` `build-failed`, `base_retry` `ran`, `head_run` `ran`; `df-e2e-rust:lib:tests::doubles` is `not-built` on the base (it calls code the pull request adds: the base gets the package with its test code alone) and `passed` on the head; `df-e2e-rust:lib:tests::already_passes_on_the_base` is `passed` on both and named in the reason; the last `gate` row is `Blocked …new-tests-fail-on-base (rejected)…`; MergeGate → Escalated; nothing merged |
| (the merging test) before the first review, commits to the PR branch `e2e-rust-merged/p<run>/Cargo.toml` (package `df-e2e-p<run>`) and `src/lib.rs`: `pub fn double`, and an inline `#[cfg(test)] mod tests` with only `doubles` (calls `double`) | the last `new-tests` row is `pass` (`passed`), its strategy list holding `rust-cargo` (beside `dotnet-xunit` for the story's own tests); `df-e2e-p<run>:lib:tests::doubles` is `not-built` on the base — how a test of code the pull request adds fails there: the base gets the package with its test code alone, so the test's own function does not compile — and `passed` on the head; the last `gate` row is `Merge …` (`passed`) listing `new-tests-fail-on-base` among its checks; a `gate-passed` row naming the head the check judged; … MergeGate → Merge → Watch; the pull request is merged at that head, its merge commit the Merge row's; the base branch's tree holds both files |

Without cargo for the worker user the `new-tests` row is `error` (`…(toolchain-missing): cargo is not available to the gate's test
runs as _factory…`) and the test fails on its first assertion, quoting that reason. The same seed runs in every `dotnet test` where
cargo is installed (`CargoRunnerTests.With_cargo_a_new_package_is_applied_to_the_base_with_its_test_code_alone`, unsandboxed), with
`With_cargo_a_new_test_failing_on_the_base_passes_and_one_already_passing_there_is_named` (the `pass` outcome),
`…a_new_inline_test_calling_code_the_pull_request_adds_does_not_build_on_the_base…`, `…in_a_workspace_below_the_repository_root…`
and `…a_base_whose_other_tests_do_not_build_is_an_error_not_evidence`. The merging test's crate is checked in every `dotnet
test` too, without cargo: `LiveHelperTests.The_crate_the_merging_cargo_test_seeds_has_one_new_test_the_cargo_strategy_names_as_the_live_test_expects`.

**Live results (2026-10-13): both PASS.** The rejection passed on its second attempt (story sc-25936, sandbox PR #49, closed unmerged by the test; `new-tests` `rejected`, strategy `rust-cargo`). The merge passed first time (story sc-25923, sandbox PR #50 merged by the gate as `3f4cc9e28ac6`; main holds `e2e-rust-merged/p5211b98a/`).
**Write the story so that every new xUnit test fails on main.** The first attempt of the rejection (sc-25922, PR #48) failed on the fixture, not the product: the story text listed assertions that already pass on main (`CountLines("a\u0085")` == 1), the implementer added them, the story's own xUnit tests were `rejected`, and the result named the `dotnet-xunit` strategy alone — `NewTestsCheck.RunAsync` returns the first strategy's failing result, so the Rust test was not in it and the test's `rust-cargo` assertion failed. Name in the story exactly the `[Fact]` methods to add, each asserting a value that is wrong on main, and say that no other test (no "still works" test) may be added.

A test that passes on the real base must never count as failing there (the review of sc-25875 reproduced five such false
passes; the rule is in CLAUDE.md: `not-built` only for what the base lacks). With cargo installed these run too, each on real
crates: `…a_new_module_file_keeps_its_imports_so_tests_of_old_code_in_it_pass_on_the_base_and_are_named` (a new `src/extra.rs`
with `use crate::add;`: both tests of old code `rejected`, the test of the new module's own function still `not-built`),
`…tests_that_exercise_only_code_the_base_has_pass_there_in_every_module_shape` (`#[cfg(all(test, not(miri)))]` modules old and
new, `mod tests { #![cfg(test)] }`, a file-level `use` the PR adds, helpers of the test module, a `macro_rules!` macro and a
derived type of the parent, `mod tests;` in its own file, a `pub(crate) use` re-export, a `#[cfg(test)] use`, an import spelt
another way, an integration test of old API: every one `passed` on the base at the first build, the check `rejected`),
`…one_import_that_does_not_resolve_on_the_base_hides_neither_a_test_of_old_code_nor_a_real_failure` and
`…an_import_of_new_code_in_a_file_the_base_has_is_left_out_and_the_tests_beside_it_get_their_real_results` (the retries).
`unsupported`, decided before any run (`CargoIdentifyTests`): a test function outside every `#[cfg(test)]` module that is new
or changed (top of a production file, a production module, a module gated by `cfg(any(test, …))`), a new `#[cfg(test)]`
`use`/`impl` outside one, cfg alternates of one name, a renamed production file with new tests.

### AT5 — freeze and stuck

Automated: `FreezeAndStuckTests`. The freeze tests write only the throwaway ledger; their story is never claimed (it stays To Do
and can be reused). The stuck tests replace the worker with a **stub Claude Code CLI** (`StubClaude`, a bash script the test
writes): it replays a recorded stream-json transcript (the unit tests' fixtures `stream-json-stuck-loop.jsonl`,
`stream-json-silence-progress.jsonl`) under a fresh session id, one line a second, and honours the factory's pause hook exactly as
the real CLI does (it parses the hook's flag path out of the `--settings` JSON with core Perl `JSON::PP` — the serializer escapes
the command's quotes, so no text match finds it — and ends the session at the next tool call once the flag exists,
`terminal_reason: hook_stopped`). `LiveHelperTests` (in every `dotnet test`) runs that parsing and the stub itself on the real
`ClaudeWorker.BuildPauseSettings` output, and checks the AT4 rounds test's expected cap. A real model cannot be made to loop on demand, so the stub is the deterministic looping worker. It
runs as the owner (`Worker:RunAs=none`, set by the test): it reads and writes nothing but its fixture and its own log, and the only
process the factory stops is that stub: through the pause flag, which the stub obeys and exits on; were it ever to need a signal,
`ClaudeWorker` signals only the process group it started, whose leader runs the configured (stub) path. The stuck stories end
Escalated with nothing pushed (move them back by hand).

| Test | Variables | Setup it does | Expected |
| --- | --- | --- | --- |
| `N_items_escalated_in_a_row_freeze_the_factory_and_the_next_dispatch_is_deferred` | `FACTORY_E2E_FREEZE_STORY` | seeds `Freeze:MaxConsecutiveFailures` (default 3) items Intake → Implement → Escalated in the throwaway ledger | the run is deferred `factory frozen (consecutive-failures): 3 items escalated in a row…`; the `freeze` control row is Paused, reason `consecutive-failures`, by `freeze`; the story has no ledger row, is still in its board state and has no PR; a second dispatch is deferred from the record alone |
| `An_unreadable_freeze_record_counts_as_frozen_and_the_dispatch_is_deferred` | `FACTORY_E2E_FREEZE_STORY` | inserts the throwaway ledger's `freeze` control row with state `corrupted` (raw SQL: no `ControlState` has it, so reading it fails) | deferred `factory frozen (freeze-record-unreadable): the freeze record could not be read…`; no ledger row, story untouched |
| `A_looping_worker_is_interrupted_at_its_next_tool_call_and_the_item_escalates_after_its_retry` | `FACTORY_E2E_STUCK_STORY` | the stub replays the looping transcript | two `stuck` rows (`failed`, `repeating tool calls: Edit`), one `stuck-retry`, transitions Intake → Implement → Escalated; both stub sessions stopped at a tool call by the pause hook, neither ran to its end; no PR |
| `A_silent_worker_that_then_makes_progress_is_not_interrupted` | `FACTORY_E2E_SILENT_STORY`, optional `FACTORY_E2E_SILENCE_SECONDS` (default 120) | the stub replays the silence-then-progress transcript with that silence before the slow test run's result | no `stuck` row; the stub session ran to its end; one `worker-done` row with its session (it then escalates: the stub changes nothing, so there is nothing to review) |

In-process versions (every `dotnet test`): `FreezeTests.Each_trigger_seeded_in_the_ledger_stops_dispatch_with_that_trigger_named`,
`An_unreadable_freeze_record_counts_as_frozen`, `The_intake_loop_dispatches_nothing_while_the_freeze_record_is_unreadable_or_set`,
`StuckWorkerTests.A_replayed_transcript_that_loops_is_interrupted_at_its_next_tool_call_and_records_a_failed_round_then_a_fresh_session_retries`,
`A_replayed_transcript_with_a_long_silence_and_then_progress_is_not_interrupted`.

### AT6 — taint: a session that read issue text cannot push

Automated: `IssueTriageTests.A_triage_session_that_read_issue_text_is_tainted_and_cannot_push`, gated like P2-AT-issues
(`FACTORY_E2E=1`, `FACTORY_E2E_ISSUES=1`, `GH_TOKEN`; the factory's App needs Issues read and write). The owner opens one fixture
issue on the sandbox; the production intake triages it in a real sandboxed worker session. Expected: the issue's item has a
`session_taints` row for that session with reason `issue-text`; `WorkLedger.GrantPushAsync` for the session throws
`SessionTaintedException` (no grant, so no installation token is minted for it); GitHub has no `factory/triage-gh-<key>` branch.
Cleanup (automatic): the issue is closed. In-process: `TaintTests.A_triage_session_holds_no_github_credential_is_tainted_and_its_push_is_refused`
(also checks the triage worker's environment holds no GitHub token, which a live run cannot observe from outside the sandbox).

# Phase 3 acceptance runbook (CodeGraph integration, epic sc-25704; sc-25710)

The terminal story writes these; they run once, live, against `michaeltrefry/dark-factory-sandbox` and the hosted CodeGraph. Each is
opt-in (`FACTORY_E2E=1` plus its own variables) and skips naming whatever is missing; each runs the production wiring against a
**throwaway ledger** and its own work root, like the tests above. They live in
`tests/DarkFactory.AcceptanceTests/CodeGraphAcceptanceTests.cs`; the wrapper the triage test uses is checked in every `dotnet test`
(`LiveHelperTests.The_scan_wrapper_counts_the_secret_by_hash_and_hands_over_to_claude`).

Prerequisites, on top of P2-AT1's (gate App, rulesets, `factory/gate.yaml`, the worker sandbox):

- the CodeGraph token in the keychain (`codegraph-token`), entitled to the reviewers' seven tools, `request_overlay`,
  `get_overlay_status`, `analyze_impact_file` and `file_hotspot`;
- the sandbox indexed in the hosted CodeGraph (`CodeGraph:BaseUrl`, default the hosted one), its project's repository URL exactly
  `https://github.com/michaeltrefry/dark-factory-sandbox`;
- CodeGraph C4 (commit-pinned reads, PR #71), C6 (overlays, PR #75) and C7 (risk tools, PR #76) deployed — P3-AT2 and P3-AT4 need
  them; P3-AT1 needs C7 deployed and its PR merged (the PR body shows the tool use only after the merge-time `pr-report`);
- for the Kanban check, a Kanban token (`kanban-token`) and the hosted KanbanBoard at `Kanban:McpUrl`.

Every `*_STORY` is a fresh To Do bug story whose fix lands in **normal** code of the sandbox (e.g. `src/Sandbox/`), with a test that
fails on the base — the P2-AT1 kind; give each test its own story.

```sh
FACTORY_E2E=1 FACTORY_E2E_P3_REVIEW_STORY=sc-<id> FACTORY_E2E_P3_OVERLAY_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*CodeGraphReviewTests"
FACTORY_E2E=1 FACTORY_E2E_ISSUES=1 GH_TOKEN=$(gh auth token) \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*CodeGraphTriageTests"
FACTORY_E2E=1 FACTORY_E2E_P3_CRITICAL_STORY=sc-<id> FACTORY_E2E_P3_UNREACHABLE_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*CodeGraphGateTests"
```

| Test | Variables | Setup it does | Expected evidence |
| --- | --- | --- | --- |
| P3-AT1 `CodeGraphReviewTests.A_review_reads_the_code_and_asks_codegraph_and_the_verdict_and_pr_body_record_it` | `FACTORY_E2E_P3_REVIEW_STORY` | none: `factory run` with the real panel | the first verdict's reviews and second opinions list `tools[]` with `read_file` and at least one CodeGraph tool, each with a 64-hex `sha256` and a `bytes` size; every review and second opinion has `turns` numbered 1…n, every one served on `high`; the item merged (needs C7 deployed: risk-threshold reads its tools) and its merge-time `pr-report` row exists — the PR body written at open predates the review, so without that row the test fails as `INCONCLUSIVE/needs merge` — and the PR body contains the tool use (`… tool call(s)…`) and equals `LedgerReport.PullRequestBody` of the ledger. A panel that made no `read_file` or no CodeGraph call fails as `INCONCLUSIVE` (the prompts ask for both; the model chooses): rerun on a fresh story |
| P3-AT2 `CodeGraphReviewTests.The_reviewers_codegraph_answers_are_for_the_head_overlay` | `FACTORY_E2E_P3_OVERLAY_STORY` | none | a `codegraph-overlay` checkpoint `ready` for the reviewed head and the verdict's `codegraph` ready; every reviewer CodeGraph call has no `fallback`, and every non-error one has `commit` = the head SHA (at least one non-error call); no CodeGraph call at all is `INCONCLUSIVE` |
| P3-AT3 `CodeGraphTriageTests.A_triage_session_uses_codegraph_through_the_proxy_and_holds_no_codegraph_token` | `FACTORY_E2E_ISSUES=1`, `GH_TOKEN` (the owner) | opens a fixture issue asking for a `search_graph` lookup; sets `Worker:ClaudePath` to a scanning wrapper (`ScanWrapper`, a Perl script under the test's work root, which the worker user reads through the work root's inherited read entry): as `_factory`, with the exact env and argv the factory gives Claude Code, it compares the SHA-256 of every window of the CodeGraph token's length in its env values, argv, the `--mcp-config` file and every file of the triage worktree with the token's hash (only the hash is written into it), prints `dark-factory-e2e-scan {…counts…}` as the session's first (raw) line, then `exec`s the real `claude` | the session's events hold a `mcp__codegraph__search_graph` tool_use whose result carries the factory's commit label ("CodeGraph's index of …"); the session that made it is tainted (a `session_taints` row for its session id; the row keeps the first reason, `issue-text` for a triage, so the MCP use is evidenced by the stored tool_use); the scan line shows 0 hits in env, argv, `--mcp-config` and worktree (and that it saw variables, arguments, the config file and files); no stored event holds the token; then, through the production proxy with a triage grant (the test as its MCP client, `OutboundHttp.LoopbackMcp`), `tools/list` is exactly `search_graph`, `get_code_snippet`, `read_node_source`, `analyze_impact` is refused with JSON-RPC -32602, `search_graph` answers with the commit label, and the Kanban endpoint is 404. No `search_graph` call is `INCONCLUSIVE`. Cleanup: the issue is closed |
| Epic AT5 Kanban `CodeGraphTriageTests.A_reviewer_session_can_call_a_kanban_read_tool_and_is_never_offered_a_write_tool` | a Kanban token | none (no model: the test plays the reviewer's tool_use blocks) | the reviewers' tools offer `list_projects` and only tools on `McpServers.KanbanTools`, no write tool; `list_projects` answers (not an error), recorded with its hash and size; `create_work_item` asked for anyway answers "There is no tool named …" and is not forwarded |
| P3-AT4 (a) `CodeGraphGateTests.A_pr_touching_a_critical_file_escalates_on_risk_threshold` | `FACTORY_E2E_P3_CRITICAL_STORY` | finds a critical file itself: every normal-tier `src/**/*.cs` file on the sandbox's main (at most 60) asked of `analyze_impact_file` at CodeGraph's default-branch index commit (`file_hotspot`'s `indexCommitSha`) until one answers `critical`; then, before the first (seeded) review, appends `// [dark-factory e2e] P3-AT4 blast-radius probe` to it on the PR branch as the workers' App | the last `gate` row is `Blocked …risk threshold: blast radius at <head> over <limit>: <file> critical…` (`gate_rejected`; the limit's wording depends on the sandbox policy's version, so the test asserts only the prefix and `<file> critical`); MergeGate → Escalated; no `gate-passed`, no Merge; the PR not merged. Needs a critical file on the sandbox's main (none found: the test skips saying so) — see **Seeding a critical file** below. Cleanup: the PR is closed and its branch deleted |
| P3-AT4 (b) `CodeGraphGateTests.With_codegraph_unreachable_the_gate_waits_then_blocks_and_does_not_merge` | `FACTORY_E2E_P3_UNREACHABLE_STORY` (and a CodeGraph token: without one the check fails at once instead of waiting) | `CodeGraph:BaseUrl` = `http://127.0.0.1:9/` (a closed port: every CodeGraph call is refused), `CodeGraph:OverlayTimeoutMinutes` = 1, `Gate:CiTimeoutMinutes` = 10 (it also bounds the CI wait before MergeGate, so this assumes the sandbox's CI finishes within 10 minutes; a slower CI escalates at CI instead and the test fails); the panel seeded | every `codegraph-overlay` row is not ready; the last `gate` row is `Blocked …CodeGraph could still not be read after 00:10:00 (Gate:CiTimeoutMinutes)…` (`gate_rejected`); MergeGate → Escalated; nothing merged. The pending evaluations themselves are not ledger rows (only decisions are, by design), so "pending" shows as the not-ready overlay rows and the Blocked reason naming the timeout. Cleanup: the PR is closed and its branch deleted |

In-process versions (every `dotnet test`): `ReviewerToolLoopTests` (tool calls with hash and size, every turn's class, the report's
tool errors), `ReviewOverlayTests`, `McpProxyTests` (the triage session's env/argv/worktree with a fake CLI, the proxy's refusals, the
commit label), `CodeGraphRiskTests` (C7's shapes field by field — a new file passes, a critical file escalates, CodeGraph unreadable
waits then blocks).

**Seeding a critical file (P3-AT4a).** CodeGraph's per-file `blastRadius` is the worst risk level among the nodes that depend on
it. Inside one repo an ordinary depth-1 caller rates `high` at most, however many callers there are, so "a helper everything calls"
never reaches `critical`. `critical` needs another repo or a message edge: a non-test depth-1 dependent in another repo, a non-test depth-2 cross-repo dependent over a `CONSUMES`/`PUBLISHES` edge, or (the only in-repo way) a non-test depth-1 class reached over a `CONSUMES` edge — which CodeGraph's C# extractor creates only for a class implementing
`IConsumer<T>`/`Consumer<T>`, and only in a repo listed in CodeGraph's `TrustedDotnetRepositories` (production: the GitHub Actions
variable `CODEGRAPH_TRUSTED_DOTNET_REPOSITORIES` on CodeGraph's `production` environment, an entry like
`github:https://github.com/michaeltrefry/dark-factory-sandbox`; a trusted repo is restored and built with MSBuild + Roslyn on
CodeGraph's host — a security tradeoff the owner accepts per repo). Untrusted repos and every overlay are syntax-only (live, the
head overlay still kept the default branch's `CONSUMES` edges: P3-AT4a saw `critical` at the head). Prerequisites, then: the sandbox
trusted in CodeGraph and re-indexed after the seed. The seed exists: fixture story sc-25847 (sandbox PR #46, merge `c64a35c`) added
`IConsumer<in TMessage>`, `TextSubmitted`, `TextStatsConsumer` and `TextFormatConsumer`. Confirm before running:
`analyze_impact_file(repo, "src/Sandbox/TextSubmitted.cs")` answers `critical` (`criticalCount` 2). After any sandbox merge
CodeGraph's default-branch index catches up on its hourly change-detecting sync or an admin "run now" (a push webhook is proposed as
CodeGraph story sc-25846).

**Live results (2026-10-12): all PASS** — P3-AT1, P3-AT2, P3-AT3, the Kanban check, P3-AT4a and P3-AT4b. Sandbox PRs #43, #44 and
#46 merged; #47 (P3-AT4a, critical file) escalated on risk-threshold, not merged; #42 (P3-AT4b) blocked at the timeout, not merged;
issue #45 was P3-AT3's fixture.

## P4-AT1 — a plan item is planned into stories, released in dependency order, merged into its changeset branch and then into the default branch (sc-25870, sc-25871, sc-25872)

`PlanAcceptanceTests.A_plan_item_is_planned_by_a_real_planner_into_stories_released_in_dependency_order_merged_into_its_changeset_branch_and_then_into_the_default_branch`:
`factory run` on a `plan`-labelled story with the real planner (a sandboxed read-only session on the `high` class, CodeGraph and
Kanban through the loopback proxy when their tokens are configured), a throwaway ledger and its own work root. Since sc-25872 it
goes all the way: each released story is really worked (`factory run` on it: implemented from the plan's changeset branch,
reviewed by the panel, merged by the gate into `factory/changeset-sc-<plan>`), and once every story has merged the plan item's own
runs open one pull request from the changeset branch to the default branch and take it through Review → CI → MergeGate → Merge.
It is the most expensive acceptance test (one implementer and one review per story, plus the changeset's review): keep the plan
story small.

Prerequisites: everything AT2 needs (router with an enrolled plan, both GitHub Apps, the Shortcut token, the sandbox set up, a
`factory/gate.yaml` on the sandbox's default branch), and `FACTORY_E2E_P4_PLAN_STORY=sc-<id>`: a **To Do** story carrying the
`plan` label whose repository is the sandbox (the default repo, or a `Repo:` line) and whose description is work of **at least
three small stories with a dependency chain** — the epic's scenario: a story that needs a story that itself needs another (A,
then B on top of A, then C on top of B) — each of which can pass the sandbox's gate on its own (e.g. each a documentation change
under the policy's free tier, or a code change with a test that fails on its base) and which together stay inside the policy's
`risk` limits — the changeset pull request is gated as a whole. The planner decides the plan, so lead it there: say in the story
that the work is three steps and that each builds on the one before. A suggested text (not yet tried on a live planner;
adapt the file names to the sandbox):

> Add a short glossary to the docs, in three steps that build on one another. First add `docs/glossary.md` with two terms. Then,
> once that file exists, add a "See also" section to it linking each term to the page that uses it. Then, once the section
> exists, link the glossary from `docs/README.md` and add one more term with its link. Each step is its own story; the second
> needs the first and the third needs the second.

A plan of fewer than three stories, or with no chain of three (three independent stories, or two that each need only the first),
fails the test as `INCONCLUSIVE: …` right after the plan is recorded and before any story is released — it proves nothing about
release in dependency order; archive the created stories (the test does) and re-run on a story worded more firmly. The sandbox's CI must run for pull requests whose base is `factory/changeset-*` (a
workflow whose `pull_request` trigger is limited to `branches: [main]` reports nothing there, and the story's CI wait times out).
The test writes to that story's board: it claims the plan story (In Progress) and creates the plan's stories in the Backlog of
its team and epic, moves them to To Do as they are released, works them (claimed, then Done as each merges into the changeset
branch), and finally reports the plan story Done with its closeout; it archives the created stories when it ends (best effort).
On GitHub it creates `factory/changeset-sc-<id>`, one pull request per story into it and one from it into the default branch,
all merged by the gate App; the changeset branch is left on the sandbox (delete it by hand, or let GitHub's "automatically
delete head branches" do it). **Stop any
`factory work` whose watch scope covers that team or epic first** (or use a team and epic nothing watches): a released story is an
ordinary To Do story, and a running factory would claim and implement it.

```sh
FACTORY_E2E=1 FACTORY_E2E_P4_PLAN_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*PlanAcceptanceTests"
```

Expected evidence: the first run succeeds with the item `Planned` (transitions Intake → Plan → Planned, no PR); `changeset` then
`changeset-pushed` rows between `plan-creating` and `plan-stories`, naming `factory/changeset-sc-<id>` in the sandbox, the branch on
GitHub at the recorded commit and no pull request for it yet; one `session` row whose
`worker-model-class` is `high` and whose `session_taints` hold `planner`; the `plan` checkpoint parses under every rule
(`Plan.FromDetail`); `plan-stories` lists one distinct id per plan story, and each of those stories, read back from Shortcut, has the
plan's title, is in Backlog, carries the planned-story marker and is blocked (`blocks` links) by exactly the stories its plan entry
names. Every later `factory run` runs no planner and creates no story: the first releases exactly the stories nothing blocks (To Do
on the board, one `plan-released` row each, the rest still in Backlog); a run with nothing newly merged adds no ledger row and
moves no story. Each released story's own run ends in `Watch` with exactly one pull request whose base is the changeset branch,
merged, a `verdict` row and a `merge-files` row naming the changeset branch as its base; after each tier the next plan run
releases exactly the stories whose blockers are all merged; while any story is unmerged the plan item has no `merged-reported`
or `closeout` row and the default branch has not moved. Once every story has merged, the plan item's next runs end in `Watch`
(… Planned → Review → CI → MergeGate → Merge → Watch): exactly one pull request from `factory/changeset-sc-<id>` into the default
branch, merged, its description starting `Changeset of plan`; a passing `verdict` on its head with every review on the `high`
class, a `gate-passed` row and a `merge-files` row whose base is the default branch; the default branch's head is the merge commit
of the plan item's Merge row; one `closeout` `posted`. **The closeouts equal the ledger's** (sc-25877, as P2-AT2 checks an item's):
each story's closeout comment on the real board is exactly `LedgerReport.MergedCloseout` rendered from that story's own rows in
the throwaway ledger (those before its posted `closeout` row), and the plan story's comment is exactly
`LedgerReport.PlanCloseout` rendered from the plan item's rows before its posted `closeout` row and each story's rows — it says
`sc-<id>'s plan is complete: n of its n stories merged` and `Its changeset merged into \`main\` as \`<merge commit>\``. A
mismatch prints both texts; a session cost the router reported after the comment was posted would show as one (rare: re-run).
Every row of the plan item and of each story carries the typed outcome `StepOutcomes.Of` gives it.

### P4-AT1 through the intake loop (`factory work`, sc-25877)

The test above drives every run itself (`factory run … --ignore-scope`), so it never shows the intake loop picking a plan up.
`PlanAcceptanceTests.P4_AT1_factory_work_picks_up_a_plan_item_in_its_watch_scope_releases_its_stories_into_it_and_picks_them_up`
does: it hosts the real `factory work` (`FactoryHost.BuildWork`, in process, a throwaway ledger and work root, the same way as
AT2's `FactoryWorkTests`) watching the plan story's scope, and starts no run of its own. The loop picks the plan item up from
the watch scope, plans it (real planner), releases its stories — created in the plan story's team and epic, so **inside the
watch scope** — and picks each released story up from `ListReadyAsync` like any ready story, until the plan's changeset pull
request has merged. It is as expensive as P4-AT1 itself (a second whole plan); run it on its own.

Prerequisites: everything P4-AT1 needs, `FACTORY_E2E_WATCH_TEAM` and/or `FACTORY_E2E_WATCH_EPIC` (the test host's watch scope,
as for AT2), and `FACTORY_E2E_P4_WORK_PLAN_STORY=sc-<id>`: a **To Do** story carrying the `plan` label, **inside that watch
scope**, worded like P4-AT1's (three steps that build on one another: the test is INCONCLUSIVE without a chain of three).
**The watch scope must hold no other To Do story, and no other `factory work` may watch it** — the host claims every ready
story it sees, and a second factory would claim this plan's released stories. The test refuses to start (before the host does)
unless the scope's ready stories are exactly the plan story.

```sh
FACTORY_E2E=1 FACTORY_E2E_WATCH_EPIC=<epic id> FACTORY_E2E_P4_WORK_PLAN_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4_AT1_factory_work*"
```

Expected evidence: the plan item reaches `Planned` with no run started by the test; every created story is inside the watch
scope (`InScopeAsync`); the plan item reaches `Watch`; it has one `session` row (planned once) and one `plan-released` row per
story, none saying `outside the watch scope`; every story has a `claimed` row recorded after its `plan-released` row (the loop's
own pickup), is linked to the plan (`PlanItemId`) and has a Merge row; every story's `plan-released` row is recorded after each
of its blockers' Merge row; exactly one changeset pull request, merged into the default branch, with a `gate-passed` row and one
`closeout` `posted`. The created stories are archived at the end (best effort); the host is stopped first.

**Live result (2026-10-13): PASS**, first attempt, 10 minutes (plan sc-25949 in a throwaway epic, 25948, as the whole watch scope): stories sc-25950 → sc-25951 → sc-25952 picked up by the loop, sandbox PRs #55 (`0c8091fa`), #56 (`149a3b43`), #57 (`d448b3b3`) into `factory/changeset-sc-25949`, changeset PR #58 merged into main as `1b4172468565`.

Not exercised live, covered by `ChangesetPipelineTests` against the simulated repository (sc-25872): a pull request whose base is
not the one the ledger expects (a story's PR switched to the default branch, an adopted PR with another base) escalates
`<url> targets <base>, not <expected>` unreviewed and unmerged; a plan story whose `Repo:` line names a repository its plan has
no changeset in escalates and opens no pull request; a changeset pull request someone merged by hand closes the plan out on its
re-run (`changeset-empty`: `changeset pull request <url> was merged outside the factory`) instead of opening another; Stop on a
plan item whose changeset pull request is open stops its unfinished stories, drafts that pull request and cancels the plan once.
Changeset size (sc-25873): a `version: 4` policy gives the changeset pull request its own limits, `risk.max_changeset_lines` and
`max_changeset_files` (both required there; the per-story `max_changed_lines`/`max_changed_files` keep judging every story's pull
request). If the sandbox's default-branch policy is still version 2 or 3, the changeset pull request of this test is held to
`max_changed_*` and escalates on `risk-threshold` unless those are sized for the plan: move the policy to version 4 (an owner's
PR: the path is sealed). The `gate` row of a changeset pull request reads `Merge <sha> (changeset pull request): …`.

CI triggers (sc-25873): before the planner runs, the factory reads the sandbox's `.github/workflows/*.yml` on the default branch.
If every workflow's `pull_request` trigger filters `branches` without `factory/**` (and none has a matching `push` trigger), the
plan item escalates at once with a `ci-triggers` row (`blocked: …`) naming the fix — add `factory/**` to the `pull_request`
`branches` filter — and the workflow files, and no story or changeset branch is created. The same holds for the changeset pull
request: workflows that run for `factory/**` bases but whose `branches` filter leaves out the default branch (and no `push`
trigger matching `factory/changeset-*`) block the plan with the fix naming the default branch. A workflow it cannot read or
parse, or a repository none of whose workflows has a `pull_request` trigger or a `push` trigger for branches (dispatch-only,
scheduled, tag releases: its CI may be another service's), is a `ci-triggers` warning row and the plan goes on.
A plan item naming several repositories (`Repo:` lines) is checked in each of them, before the planner runs: one that blocks
stops the whole plan (the reason starts with that repository; no story or changeset branch in any of them), and a warning is
recorded per repository.

In-process versions (every `dotnet test`): `ChangesetPolicyTests` (version 4 parsing, both limits for both kinds of pull request
under versions 2, 3 and 4), `ChangesetPipelineTests` (the changeset fact from the ledger: a plan's changeset pull request against
`max_changeset_*`, a story whose base is a `factory/changeset-*` branch and an item of no plan against `max_changed_*`; a plan
blocked, warned or planned by its repository's workflows), `CiTriggerTests` (every `on:` shape, GitHub's branch filter matching,
the GitHub read).

When `write-lifecycle.json` is re-recorded (`SHORTCUT_RECORD=1`, the owner's run), confirm two things about the idempotency key
the factory sends as each created story's `external_id` (sc-25877; `dark-factory:child:<parent>:<64 hex characters>`, 92
characters for a parent such as `sc-25858`, one more per extra digit of the parent's id): that `POST stories` accepts an
`external_id` of that length and returns it unchanged (not cut, not refused), and that the story listings the factory reads
(the epic's and the team's stories) carry `external_id` for such a story — a retry finds its children from the listing by it.
Both confirmed on 2026-10-13 (sc-25877): `write-lifecycle.json`'s two `POST stories` carry a 92-character key and
their 201 responses return it unchanged; `release.json` (recorded right after, on the same throwaway stories) holds the retry
— the team's listing carries each child's `external_id`, both are found, nothing is created — and the live P4-AT1 stories
show it in the epic's listing (`GET epics/{id}/stories`) too.

**Live result (2026-10-13): PASS**, first attempt, 11 minutes (plan sc-25939: three `TextTools` helpers, each calling the one before, the story text saying "plan exactly these three stories, the second blocked by the first, the third blocked by the second"): stories sc-25941 → sc-25942 → sc-25943, sandbox PRs #51 (`c0f15801`), #52 (`bb572c7c`), #53 (`3f791318`) into `factory/changeset-sc-25939`, changeset PR #54 merged into main as `344a199ffd28`. A code plan works as well as the documentation one suggested above. Written with sc-25870 and extended with sc-25871. The live run was also the
first time the release's Shortcut requests meet the real API: `GET stories/{id}` returning a story with `story_links`, and
`PUT stories/{id}` with only `workflow_state_id` (Backlog → To Do) had no recorded contract fixture then; since 2026-10-13 `release.json` records both (`ShortcutContractTests.A_retry_finds_its_children_by_external_id_and_a_release_reads_blockers_and_moves_backlog_to_to_do`); the
unit tests cover them against the fake board and against the story shape the `list-ready.json` recording holds. Two more reads
first meet the real API there: a story's blockers are each read with their own `GET stories/{id}` and judged finished by the
`type` (`done`) of their workflow state in `GET workflows` (the recording's workflow carries those types), and a released story
the ledger does not know yet is asked for once per run (`GET stories/{id}`: is it back in the Backlog?). The test's runs between
tiers therefore read the released stories; they still add no ledger row and move no story.

What the live test does not cover, and the unit tests do (`PlanReleasePipelineTests`, `PlanRunnerLockTests`): a blocker outside the
plan (held until the factory merges it or the board shows it finished), an archived story, a released story moved back to the
Backlog, a plan whose stories were never recorded (a marked story is refused at claim), a plan that cannot be evaluated (one comment
and one `plan-held` row after `Intake:MaxItemFailures` failures), a closeout the board keeps refusing (the item stays `Planned`,
"post it by hand"), and a Stop landing on a waiting plan while it is evaluated (finished by that run, sc-25988).

## P4-AT2 — a plan across two repositories: a changeset in each, merged in the plan's order (sc-25873)

`MultiRepoPlanAcceptanceTests.P4_AT2_a_plan_across_two_repositories_gets_a_changeset_in_each_and_merges_them_in_the_plans_order`:
`factory run` on a `plan`-labelled story that names two repositories, with the real planner seated in both, a throwaway ledger
and its own work root. The test then drives the plan item and every released story until the plan item is in `Watch`. It is more
expensive than P4-AT1 (one implementer and one review per story, plus one changeset review per repository): keep the work small.

Prerequisites: everything P4-AT1 needs, and
- `FACTORY_E2E_P4_SECOND_REPO=owner/name`: a second sandbox repository with **both GitHub Apps installed** (the workers' App and
  the gate App), protected with `factory github-repo protect owner/name`, a `factory/gate.yaml` on its default branch, CI that
  runs for pull requests whose base is `factory/changeset-*`, and **indexed in CodeGraph** (the gate's `risk-threshold` and the
  planner's tools read it). The test adds it to the factory's configured repositories for its own runs (`Factory:Repos`);
  for a real `factory work` set `Factory__Repos=owner/name` too (comma-separated for several). `Factory:Repos` is the setting
  for this: it lets the factory plan and work there and turns on no issue polling. `GitHub:Watch:Repos` also makes a
  repository configured, but it is the issue-triage watch list — it starts polling and triaging that repository's issues, and
  `factory work` refuses it with `Worker:RunAs=none` — so use it only when issue triage there is wanted.
- `FACTORY_E2E_P4_MULTI_PLAN_STORY=sc-<id>`: a **To Do** story carrying the `plan` label whose description has two lines,
  `Repo: <the sandbox>` then `Repo: <the second repository>`, and describes work that needs a small change in each (e.g. a
  documented format in the first and a reader of it in the second), each story able to pass its repository's gate on its own and
  each repository's changeset inside that repository's `risk` limits. **At least one story must wait on the first repository's
  changeset** (`blocked_by_changesets` in the plan): say in the story that the second repository's change must only start once
  the first repository's change is on its default branch — e.g. "the change in `<second>` must only start after the change in
  `<first>` is on `<first>`'s default branch". State the order as a release rule and spell the shared contract out in the story
  itself; do **not** ask for the second repository's change to be "written against" or "read from" the first repository: a
  story's worker sees only its own repository's checkout, and a story that tells it to read the other one ends `Worker finished
  without changing the repository` (the first live run, 2026-10-13; the planner is now told so, sc-25877). A plan in which no story waits on a
  changeset fails the test as `INCONCLUSIVE: …` right after planning (sc-25877): the check "released only after that changeset's
  merge" would otherwise check nothing.
Stop any `factory work` whose watch scope covers the story's team or epic first, as for P4-AT1.

```sh
FACTORY_E2E=1 FACTORY_E2E_P4_MULTI_PLAN_STORY=sc-<id> FACTORY_E2E_P4_SECOND_REPO=owner/name \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*MultiRepoPlanAcceptanceTests"
```

Expected evidence: the first run ends `Planned`; the `plan` checkpoint has a `repo` on every story, a `merge_order` of both
repositories and a non-empty `interactions` (a planner that put every story in one repository makes the test INCONCLUSIVE, and
so does a plan with no story that has a `blocked_by_changesets` entry);
one `changeset` + `changeset-pushed` pair per repository, all before `plan-stories`, each naming `factory/changeset-sc-<id>`;
every created story's first `Repo:` line is the repository its plan entry names. Each released story's own run ends in `Watch`
with exactly one merged pull request in its own repository whose base is the changeset branch. No story with a
`blocked_by_changesets` entry is released before that repository's `changeset-delivered` row; the second repository's changeset
has no pull request while the first is not delivered. The plan item's transitions hold two `Merge` rows with a `Planned` row
("the changeset of <first> merged as …") between them and end in `Watch`; `changeset-delivered` rows name the repositories in
merge order, each `merged` with its pull request; each repository has exactly one merged changeset pull request whose description
says `The plan changes 2 repositories.`; the two `merge-files` rows name their repositories; one `closeout` `posted` whose comment
lists both repositories as `merged into`.

**Live result (2026-10-13): PASS on the second attempt** (plan sc-25971, second repository `michaeltrefry/dark-factory-sandbox-2`): sandbox PR #61 (`a824de0b`) into its changeset branch, changeset PR #62 merged into main as `9ad869946ac8`; only then the downstream story, dark-factory-sandbox-2 PR #1 (`26be3a1c`), and its changeset PR #2 merged into main as `b7e1d51fcf53`.
**Do not ask a downstream worker to read the upstream repository.** The first attempt (plan sc-25940) failed after the upstream half was delivered (sandbox PRs #59 and #60, main `20b0c5eed669`, which therefore holds `IsPalindrome` and the second sandbox does not): the story text said the downstream change must be "written against the method as released on" the first repository's default branch, the planner wrote a story telling its worker to read the method "from upstream main", and that worker — seated in its own repository alone — ended `Worker finished without changing the repository; nothing to review` (Escalated at Implement). The planner of several repositories is now told what a story's worker sees (sc-25877, `PlanPrompt`), and the second attempt's story text gave the shared contract in full and stated the order as a release rule only. Not
exercised live, covered by `MultiRepoPipelineTests` against two simulated repositories: an upstream changeset pull request that
escalates (the downstream one is not opened; `plan-delivery` and the comment say what is delivered), a downstream one that
escalates after the upstream merged (partial delivery, then fixed and re-run), Stop with one changeset merged and one open, a run
that died after the first repository's branch or between the two changesets, an empty changeset skipped in the order, a
hand-moved story refused while it waits on a changeset; and by `MultiRepoPlanTests`: every validation refusal.

Also covered only by `MultiRepoPipelineTests` (review fixes, 2026-10-13), none exercised live: the merge order when a later
repository's stories merge first (its changeset pull request is not opened before the earlier one's `changeset-delivered`); a run
that loaded the plan item before another process moved it to the next repository (the pass's repository is decided under the run
lock); an item whose linked pull request is in another repository than the run's (escalates; nothing read or merged there); a
second pass not held by the first one's CI triage, fix-round cap or `gate-passed` (a changeset pull request found merged counts
only by a `gate-passed` of its own pass naming that head); the plan item carrying the recorded spelling of the pass's repository
and its primary repository again outside a pass; a plan whose stories are all in its second repository (gated and merged there,
`merge-files` names it, Stop drafts its pull request); a `plan` checkpoint that no longer reads back (nothing released, claims
refused); a plan story whose `Repo:` line was edited to the plan's other repository (escalates); and the wording of a CI-trigger
block or "not configured" refusal on a run that continues a plan whose stories already exist. When P4-AT2 is run live, check on
the plan item's row that `Repo` is the first `Repo:` line's repository once it is back in `Planned` and in `Watch`, and the
second repository's only between its changeset pull request opening and merging.

## P4-AT3 (proxy part) — a planner grant over two repositories (sc-25874)

`PlanAcceptanceTests.P4_AT3_a_planner_grant_over_two_repositories_answers_for_each_and_refuses_a_third_project_and_off_list_tools`:
the production upstream registry and loopback proxy with a planner grant over two repositories; the test plays the planner's MCP
client itself, so no model is called and nothing is written anywhere.

Prerequisites: a CodeGraph token (`CodeGraph:Token`) and `FACTORY_E2E_P4_PLAN_REPOS=owner/a,owner/b`: two repositories the hosted
CodeGraph indexes (each project's repository URL exactly `https://github.com/<owner>/<name>`), e.g. SceneWorks and inference;
CodeGraph must index at least one more project (the "third").

```sh
FACTORY_E2E=1 FACTORY_E2E_P4_PLAN_REPOS=owner/a,owner/b \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4_AT3*"
```

Expected evidence: `tools/list` shows only planner-profile tools, the six cross-repository ones among them, `search_graph` with a
`repo` enum of the two repositories; a `search_graph` answer for each repository, labelled with that repository's CodeGraph
project; and JSON-RPC -32602 for a `project` naming a third indexed project (alone, and against a `repo` of the set), a `repo`
outside the set, a call naming no repository, and `get_architecture`, `get_service_summary` and `rag_search`.

**Live result (2026-10-13): PASS**, first attempt, with `FACTORY_E2E_P4_PLAN_REPOS=michaeltrefry/dark-factory-sandbox,michaeltrefry/dark-factory-sandbox-2` (both indexed in the hosted CodeGraph).

## P4-AT3 (the refusals) — a cycle, an oversized plan, an unconfigured repository (sc-25877)

Three tests in `PlanAcceptanceTests`, each `factory run` on a real `plan`-labelled story with a throwaway ledger and its own work
root. In each the code refuses the plan, the item is `Escalated`, and **nothing is created**: no `plan`, `plan-creating`,
`plan-stories`, `changeset` or `plan-released` row, no `factory/changeset-sc-<id>` pull request, and not one story more in the
plan story's epic and team on the real board than before the run (the test lists both before and after through the Shortcut
API; do not add stories there while it runs).

| Test | Variable (a **To Do** story carrying the `plan` label) | What it does | Expected on the ledger |
| --- | --- | --- | --- |
| `P4_AT3_a_plan_with_a_dependency_cycle_is_rejected_and_nothing_is_created` | `FACTORY_E2E_P4_CYCLE_STORY` — on the sandbox repo; its text does not matter | the planner is a **stub Claude CLI** (as P2-AT5's stuck worker: `Worker:ClaudePath` = a script the test writes, `Worker:RunAs=none`) that answers a fixed plan — `a` needs `b`, `b` needs `a` — through the production planner seat (real checkout, real proxy grant when CodeGraph/Kanban are configured) | Intake → Plan → Escalated; one `session` row, the stub's, run to its end; one `plan-rejected` row (`gate_rejected`) saying `the stories block each other in a cycle: …`; the escalation reason `The planner's answer is not a valid plan, so no story was created: …` |
| `P4_AT3_an_oversized_plan_is_rejected_and_nothing_is_created` | `FACTORY_E2E_P4_OVERSIZED_STORY` — likewise | the stub answers a plan of 21 stories (`PlanParser.MaxStories` + 1) | the same, the `plan-rejected` row saying `the plan has 21 stories; at most 20 are allowed` (refused whole, never cut to 20) |
| `P4_AT3_a_plan_naming_an_unconfigured_repository_escalates_before_any_planner_session_and_nothing_is_created` | `FACTORY_E2E_P4_UNCONFIGURED_STORY` — its description has **one line `Repo: owner/name`** naming a repository the factory is not configured for (not `Factory:DefaultRepo`, not in `Factory:Repos`, not in `GitHub:Watch:Repos`); the repository need not exist | the real, sandboxed set-up; no stub. Since sc-25877 a single unconfigured `Repo:` line is refused like one among several (epic E1) — the single-line form is the one to use here; a story with several lines of which one is unconfigured passes too | Intake → Plan → Escalated; the reason `The plan names 1 repository (owner/name), but owner/name is not configured for this factory (Factory:DefaultRepo, Factory:Repos, GitHub:Watch:Repos: …), so no planner session was started and no story was created. Add it to Factory:Repos (GitHub:Watch:Repos would also turn on the triage of its issues), or remove its Repo: line.`; **no** `session`, `worker-started`, `worker-model-class` or `plan-rejected` row and no `worker_sessions` row; the escalation comment on the story says so. A story naming only configured repositories is INCONCLUSIVE |

Prerequisites: what `factory run` needs (router with an enrolled plan — the usage check runs at start-up even with the stub —
the Shortcut token, both GitHub Apps, the sandbox set up with CI that runs for `factory/changeset-*` bases: the CI-trigger
check runs before the planner for the two stub tests). The stub tests run the planner **as the owner** (`Worker:RunAs=none`): the
only process started is the test's own script, which reads its fixed transcript and writes its own log. Each test leaves its
story Escalated on the board (In Progress, claimed by the factory, one escalation comment). Read from the code (not confirmed
at a live run yet), the story need NOT be moved back to To Do before that test runs again: each run has a throwaway ledger, so
the item is new and is claimed again, and `ShortcutWorkSource.ClaimAsync` accepts a story that is already the factory's — its
owner and the `factory-claimed` label, which an escalation leaves on it — whatever its state (`ClaimRefusalAsync` asks for To Do
only of a story the factory does not hold; these tests run with `--ignore-scope`). Each further run adds one more escalation
comment (the tests look for theirs among them). Moving it back to **To Do** is needed only if someone took the factory off as
owner or removed the label — the claim is then refused, "the story is no longer in To Do" — and is harmless otherwise. Give
each test its own story, or run them one at a time on one story (the unconfigured-repository test needs a `Repo:` line the other two must not have).
**Stop any `factory work` whose watch scope covers those stories first**, as for P4-AT1.

```sh
FACTORY_E2E=1 FACTORY_E2E_P4_CYCLE_STORY=sc-<id> FACTORY_E2E_P4_OVERSIZED_STORY=sc-<id> FACTORY_E2E_P4_UNCONFIGURED_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4_AT3*"
```

(The filter also runs the proxy part above, which skips without `FACTORY_E2E_P4_PLAN_REPOS`.)

In-process versions (every `dotnet test`): `PlanPipelineTests.A_planner_answer_that_is_not_a_valid_plan_escalates_the_item_and_creates_no_story`
(every rule), `A_plan_naming_an_unconfigured_repository_escalates_before_any_session_and_creates_nothing` (several lines),
`A_plan_whose_single_repo_line_names_an_unconfigured_repository_escalates_before_any_session_and_creates_nothing` (one line), and
— the live tests' own fixtures — `PlanLiveTests` (the two fixed answers are refused for exactly the reasons above; the stub CLI
hands its fixed answer to the pipeline as a session's result under a fresh session id).

**Live results (2026-10-13): all three PASS**, first attempt, each on its own story (cycle sc-25917, oversized sc-25919, unconfigured repository sc-25920 with `Repo: michaeltrefry/p4a-live-not-configured`): each left Escalated on the board with one escalation comment carrying the reason above, and nothing created.

## P4-AT5 — a failed story holds its dependents, and Stop on a plan stops its stories (sc-25876)

`PlanFailureAcceptanceTests.P4_AT5_a_failed_story_holds_its_dependent_the_plan_item_says_so_once_and_stop_on_the_plan_stops_its_stories`:
a fixed plan — A, B that needs A, C and D that need nothing — whose stories the test creates on the real board through the work
source (no planner, no model, no worker), with a throwaway ledger seeded at `Planned` in the shape production leaves it
(sc-25877): `plan`, `plan-creating`, then the plan's `changeset` and `changeset-pushed` rows (`factory/changeset-sc-<plan>`;
only the rows — nothing is pushed to the sandbox), then `plan-stories`. The stories' own progress is seeded in that
ledger, which is all the plan reads: A Escalated ("seeded by P4-AT5: CI is red"), C in flight (Review), D merged.

Prerequisites: what `factory run` resolves before it starts (the router key, both GitHub Apps' ids and keys, the Shortcut token),
though no router call is made; the workers' GitHub App installed on the default (sandbox) repo with pull-request read (the stop
asks GitHub for open pull requests of `factory/sc-<story>` and, for the plan item, of its changeset branch
`factory/changeset-sc-<plan>`, and finds none — the test checks first that the sandbox has no pull request of that branch); and `FACTORY_E2E_P4_HOLD_STORY=sc-<id>`: any story on
the sandbox's board (it need not carry the `plan` label and is not planned or worked). The test writes to that story's board: it
creates four stories `[dark-factory P4-AT5] …` in the Backlog of that story's team and epic, moves three to To Do, comments twice
on the named story and moves it to the **Backlog** (the stop), moves A and C back to the Backlog with a comment each, and archives
the four created stories when it ends (best effort). **Stop any `factory work` whose watch scope covers that team or epic first**:
the released stories are ordinary To Do stories for the minute the test runs.

```sh
FACTORY_E2E=1 FACTORY_E2E_P4_HOLD_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4_AT5*"
```

Expected evidence: the first run releases A, C and D (To Do on the board; B in Backlog). After the seeding, three more runs leave
the item `Planned` with exactly one `plan-held` row `held by a failed story: sc-<A> escalated at …: \`seeded by P4-AT5: CI is red
…\`; not released because of them: sc-<B> (behind sc-<A>)` (outcome `deferred`), one `plan-notice` row `posted`, no Escalated row
on the plan item, B still in Backlog, and exactly one new comment on the plan story ("… (plan) is held by a failed story", A, its
reason, both remedies). `DashboardData` over that ledger shows the plan row with the four stories beneath it in plan order: A
`Escalated` / `failed: escalated`, B `not released` / `held by sc-<A> (escalated)`, C `Review`, D `Watch`; counts 1 merged, 1 in
flight, 1 waiting, 1 failed; held, not stuck. `factory stop --item sc-<plan>` (`FactoryRunner.ControlAsync`) answers Ok (exit 0):
A and C are `Cancelled` with `prs-drafted` and `stop-reported` rows, in Backlog on the board, each commented "… was stopped by
P4-AT5 (with its plan sc-<plan>)"; D is still `Watch`, uncommented and where it was; B has no ledger item, no comment, and is in
Backlog; the plan item is `Cancelled` — stopped as a plan with a changeset is: its recorded changeset read back, one `prs-drafted`
row `none` (its changeset branch has no pull request), one `stop-reported` row — its story in Backlog with one more comment
listing `Stories stopped with it: sc-<A>, sc-<C>. Already merged (left as they are): sc-<D>. Never started (…): sc-<B>.` and
ending `It had no open pull request. Branch factory/changeset-sc-<plan> is kept; nothing was merged or deleted.`; its control
row is gone; `InFlightAsync` lists
nothing; a second stop writes nothing.

**Live result (2026-10-13): PASS**, first attempt, 13 seconds (hold story sc-25921; the four seeded stories sc-25927 to sc-25930 archived by the test). The same class holds one test that is not live and
always runs (the fixed plan passes every plan rule).

What the live test does not cover, and the unit tests do (`PlanClassificationTests`, `PlanFailurePipelineTests`,
`PlanControlsPostgresTests`, `DashboardComponentTests.A_plan_row_shows_…`): a pull request actually turned back into a draft by
a plan's stop (the seeded stories have none; the unit test's story has one), a story whose run is going when the Stop lands
(stopped by that run; the plan's stop unfinished, exit 1, finished by the next poll), a stop that fails half way, a story whose
worker a crashed run left, a Stop that lands while the plan is evaluated (finished by that
run) and `factory stop` on a plan whose own run is active (unfinished, exit 1), a released story
nobody claimed (moved back to the Backlog with a comment that does not say the factory worked on it), one a person took, finished,
assigned or archived since its release (nothing written, no ledger item), one moved back to the Backlog (nothing written), a
story merged outside the factory (left as it is and listed apart), a plan Pause with the usage pause also in force (`user-paused`),
a plan Pause in the merge queue (`MergeQueueTests.An_idle_story_whose_plan_is_paused_…`), the control read's query count and its
failing closed, the plan row's `hold comment not posted` note, Pause and Continue on a plan (an in-flight story paused at its
next tool boundary and resumed in the same session; nothing claimed or released meanwhile), a transitive hold, a stuck plan, a
story merged outside the factory, a cancelled story, a failed story that is re-run and merges (`plan-resumed`, its dependents
released), a comment the board refuses (retried, bounded, never posted twice), a story of a stopped plan moved to ready by hand
(claim refused), and the rendered pipeline page.

## P4b-AT1 — a Ready Kanban item becomes a merged pull request with no human (sc-25981, sc-25992)

`KanbanAcceptanceTests.P4b_AT1_a_ready_kanban_item_becomes_a_merged_pull_request_with_no_human_and_its_closeout_is_the_ledgers`:
the epic's P4b-AT1 — "A Ready item in a watched Kanban project becomes a merged pull request with no human: claimed, InProgress,
PR and branch links on the item, Done, a closeout comment equal to the ledger's." A real KanbanBoard item goes through
`FactoryRunner.RunAsync` (what `factory run kb-N` calls) in ONE run — implement, the real review panel, CI, the merge gate and
the merge queue — to Watch, against a throwaway ledger and work root, as Phase 2's P2-AT1 (`ReviewGateTests`) does for a
Shortcut story.

Prerequisites: everything `factory run` needs (router key and an enrolled plan, both GitHub Apps installed on the target
repository with its rulesets — the gate must be able to merge — and the sandbox's `factory/gate.yaml` on `main`, Claude Code for
the worker user) — no Shortcut token: since sc-25982 the shared harness asks only for the credential of the test's own work
source (`E2e.StartAsync(prefix, ItemNaming.Kanban)`) — and

- `Kanban:WriteToken` (env `FACTORY_KANBAN_WRITE_TOKEN` or keychain account `kanban-write-token`): a `full`-scope KanbanBoard
  personal access token of the factory's service identity (KanbanBoard's `docs/deployment.md`, "Factory service identity");
- `FACTORY_E2E_P4B_KANBAN_PROJECT`: the key of a KanbanBoard project whose `repository_url` names the sandbox repository
  (`https://github.com/<owner>/<name>.git`), or whose items carry a `Repo: owner/name` line;
- `FACTORY_E2E_P4B_KANBAN_ITEM`: the id (GUID) of an item of that project in `Ready`, unclaimed, with no open blocker, describing
  a SMALL bug fix in normal code of the sandbox (one the gate will merge, as for P2-AT1: a one-line fix the sandbox's tests can
  show, with a new test). The item is used up: use a fresh one each run.

```sh
FACTORY_E2E=1 FACTORY_E2E_P4B_KANBAN_PROJECT=<KEY> FACTORY_E2E_P4B_KANBAN_ITEM=<GUID> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4b_AT1*"
```

What it checks: the item gets one `kb-<n>` however often it is asked for and is listed ready under it in the watched project;
the run succeeds and the throwaway ledger's transitions start Intake → Implement (claimed, then InProgress on the board) and end
MergeGate → Merge → Watch (fix rounds between are the panel's call), every row with its typed outcome; one `claimed` and one
`linked` row (`<pull request> <branch>`, the branch `factory/kb-<n>`; a throwaway ledger starts its Kanban keys at the seconds
since 2026-01-01, `E2e.KanbanKeyStart`, so no two runs share an item's branch — sc-25992) and a `merged-reported` row; on GitHub the pull request
from `factory/kb-<n>` is merged at the commit the Merge row records; on the board the item is `Done`, with the pull request and
the branch as links of kind `pr` and `branch`, and EXACTLY ONE comment equal to `LedgerReport.MergedCloseout`'s rendering of
the rows written before the `closeout` `posted` row (attributed, as the pipeline posts it); a second run adds no second
`claimed`, `linked` or posted `closeout` row and no link, and the closeout stays the item's only closeout. Its one board write
is the factory's once-only notice on an item nothing in this phase moves on (`RunPipeline.HeldNoticeText` for Watch, with a
`held-notice` row): the second run must not be deferred and must succeed, and the test asserts exactly one posted
`held-notice` row, exactly one such comment and no other comment. The notice is deterministic: Watch is no handled state, so the
run goes to `HoldAsync`, which posts it once per state entry (the first run enters Watch without one); it would be missing only if
the freeze deferred the run or the board refused the comment (logged, no row, retried by the next run) — both reported as
failures.

Not live (every `dotnet test`): `KanbanWorkSourceTests` (each member, each refusal, paging, what every write sends — against a
fake MCP server that behaves as KanbanBoard's contract says, `FakeKanbanBoard`), `KanbanPipelineTests` (the same run through the
pipeline with fakes, and the resume), `KanbanWriteIsolationTests` (E2) and `KanbanKeysPostgresTests` (the migration, and
processes allocating one item's key at once).

## P4b-AT2 — a `plan`-labelled Kanban item becomes items with dependencies, worked in order and merged by one changeset pull request (sc-25984)

`KanbanPlanAcceptanceTests.P4b_AT2_a_plan_labelled_kanban_item_becomes_items_with_dependencies_worked_in_order_onto_a_changeset_branch_and_merged_by_one_gated_changeset_pull_request`:
a real KanbanBoard item labelled `plan` through `FactoryRunner.RunAsync`, run by run like P4-AT1 (`factory run kb-N` for the
plan item, then for each released item), against a throwaway ledger and work root: a real planner session, the real board, real
workers, the real review panel and merge gate.

Prerequisites: as P4b-AT1 (the router and an enrolled plan, both GitHub Apps on the target repository with its rulesets — the
gate must be able to merge —, Claude Code for the worker user, `Kanban:WriteToken`, `FACTORY_E2E_P4B_KANBAN_PROJECT`; no
Shortcut token), a target repository whose CI runs for pull requests into `factory/changeset-*` (as for P4-AT1), and

- `FACTORY_E2E_P4B_PLAN_KANBAN_ITEM`: the id (GUID) of an item of that project that carries the label `plan`, is in an epic, in
  `Ready`, unclaimed, with no open blocker, and describes three or more steps that build on one another (each step needing the
  one before), all in the sandbox repository. Use a FRESH item for every run: the planned items' idempotency keys derive from
  the plan item's GUID and their own text, so a second plan of the same item answers the first run's items again where the
  planner repeats itself. No other `factory work` may watch the project while it runs.

```sh
FACTORY_E2E=1 FACTORY_E2E_P4B_KANBAN_PROJECT=<KEY> FACTORY_E2E_P4B_PLAN_KANBAN_ITEM=<GUID> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4b_AT2*"
```

What it checks. The first run ends Planned after one planner session. The plan has at least 3 stories with a dependency chain —
otherwise the run fails INCONCLUSIVE (`PlanLive.ChainProblem`, as for P4-AT1): describe work whose steps build on one another.
`plan-stories` names one `kb-<n>` per planned item, each a distinct item of the real board that is not the plan item: its title
the plan's, its type the board's for the planner's (`Story`/`Issue`/`Task`), in the plan item's project and epic, in `Backlog`,
unclaimed, its description led by the marker naming the plan item's GUID, and blocked by exactly the items the plan says (the
board's own dependencies). The changeset branch is `factory/changeset-kb-<n>` (the plan item's ledger name) with no pull
request yet. Then, until every item has merged: a run of the plan item stays Planned, plans nothing and creates nothing, and
the released items are exactly those whose blockers have merged — `Ready` on the board, one `plan-released` row each, none
saying "outside the watch scope" — while the others stay in `Backlog`; a second run with nothing newly merged adds no row; each
released item's own run ends in Watch with its pull request merged into the changeset branch (`merge-files` names it), a
verdict, a `gate-passed` row, typed outcomes on every row, a `claimed` row no earlier than its release, and the item `Done` on
the board. Finally the plan item's runs take it Planned → Review → CI → MergeGate → Merge → Watch: exactly one pull request from
the changeset branch, into its recorded base, merged, with `gate-passed`; one `plan-creating` row and the same `plan-stories`
as at the start (nothing was created twice); the plan item `Done` on the board with that pull request as a `pr` link and one
closeout comment equal to `LedgerReport.PlanCloseout`'s rendering of the throwaway ledger's rows.

It leaves the planned items `Done` (merged) on the board; any a failed run left in `Backlog` or `Ready` are moved to `Closed`
(best effort) so nothing picks them up afterwards. The changeset and item branches stay on the sandbox, as for P4-AT1.

Not live (every `dotnet test`): `KanbanPlanSourceTests` (child creation — once, in the parent's project and epic, the key and
the marker from board ids; a retry after a failure part-way at a create and at a dependency; a lost answer; a namesake never
reused; a cycle refused before any call and by the board; blockers, with a `Closed` one still blocking; release, with a
conflict; the return of an unstarted item; the type mapping), `SourceTrustTests` (the trust decision per source — prompt, commit
and pull request name, pull request body — for Shortcut, Kanban, GitHub issues and planned items on each), and
`KanbanPlanPipelineTests` (over the fake board and a simulated repository through the real pipeline and gate: a plan item
planned into Backlog items with a changeset branch; a re-run after the board failed part-way; a chain released and worked in
order, a hand-moved item refused at claim naming its blocker, the changeset pull request and the closeout; a blocker dragged to
`Done` by hand; an outside blocker and a `Closed` planned item; Stop returning an unstarted item; a cycle the board refuses).

## P4b-AT3 — a held claim, its renewal, and a stale write (sc-25983)

`KanbanAcceptanceTests.P4b_AT3_a_held_claim_refuses_a_second_caller_is_renewed_past_its_first_expiry_and_a_stale_write_changes_nothing`:
the real board with two callers — the factory's identity (`Kanban:WriteToken`) and a second one — against a throwaway ledger.
No model runs and no pull request is opened: the item is a throwaway one the test creates in the `Backlog` (never `Ready`, so
no factory takes it), it is in flight only in the throwaway ledger (seeded as Intake leaves a claimed item: a `claimed`
checkpoint, then Implement), and the intake loop's runs are recorded, not started (`SeededRunner`: the production
`RunPipeline.InFlightAsync`, `RunPipeline.RenewableAsync` and `RunPipeline.ClaimLostAsync`, a no-op `RunAsync`).

Prerequisites: the shared harness's (as P4b-AT1), `Kanban:WriteToken`, and

- `FACTORY_E2E_P4B_KANBAN_PROJECT`: the key of a KanbanBoard project the test may create an item in (P4b-AT1's will do);
- `FACTORY_E2E_P4B_KANBAN_OTHER_TOKEN`: a `full`-scope KanbanBoard personal access token of ANOTHER identity than the factory's
  service identity (a person's, or a second service identity's). The same identity would not be a second caller: its claim under
  another marker is refused too, but a lost claim would not name anyone else.

```sh
FACTORY_E2E=1 FACTORY_E2E_P4B_KANBAN_PROJECT=<KEY> FACTORY_E2E_P4B_KANBAN_OTHER_TOKEN=<token> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4b_AT3*"
```

What it checks, in order (it takes about half a minute — it waits out a 20-second claim):

| Step | Expected |
| --- | --- |
| `create_work_item` twice with one idempotency key | one item (`created` true, then false with the same id), in `Backlog` |
| the factory claims the item for 20 s under `dark-factory:<item GUID>`; the second caller claims it | the second caller's claim is refused `already_claimed` |
| one intake poll (`IntakeLoop.PollOnceAsync` over the real Kanban source) | the claim still carries the factory's marker and identity, and now expires more than 23 hours after its first expiry; nothing reported lost |
| 25 s later (past the first expiry) | still the factory's; the second caller is still refused `already_claimed` |
| the second caller moves the item to `InProgress` on the version the factory read; the factory then writes `Done` on that version | refused `conflict`; the item is `InProgress` at the second caller's version: nothing was written |
| the factory's claim is released, the second caller claims the item, one more poll | the ledger item is Paused, its last transition ``claim lost: the item is claimed by `<their name>` (marker `p4b-at3-second-caller`)`` (outcome `deferred`; the name and the marker are the other caller's text, each an inert code span), then a `parked` row; on the board their claim, the column and the version are unchanged; the item is no longer listed in flight |

Cleanup (best effort, in a `finally`): both claims released and the item moved to `Closed`, which hides it from the board. The
factory's comment on the item (who holds it, what resumes it) stays on it.

Not live (every `dotnet test`): `KanbanSafeWritesTests` against `FakeKanbanBoard` — each error code as a typed refusal for
every write the caller sends (and a code the contract does not name as no typed refusal), `busy` retried with the same call
and its bound, the read before a repeat for each write (a lost
answer as a plain MCP error, an HTTP error and a broken answer; a write that did not happen; a second failure; a read that
fails), conflicts on the claim's move and on a reported state (an edit kept, someone else's move left standing, the re-read
bound, after which the claim is given back), renewal across polls with the board's clock moved (held past the first lifetime,
a lapsed claim taken again — but not on an item the ledger never recorded as claimed, nor on one moved out of `InProgress` —, a
claim lost to another caller, to a race, to another identity under the same marker, an item gone; a hostile claimant name and
marker cut and inert), the pause and its one comment (and a comment the board refuses, on record in the `parked` row), what is
renewed (every item the ledger resumes by itself, also under a factory, epic or item Pause, the freeze and the usage pause, and
with unreadable controls — and nothing is run then) and what is not (a parked, an escalated and a stopping item), a
`factory run` of a claim-lost item (refused while the other claim stands or the board will not confirm it, resumed once it is
released), and the poll's renewal over several lanes (a board that does not answer ends only its own lane's renewals).

## P4b-AT4 — `factory work` starts with no Shortcut token and works a Kanban item (sc-25982)

`KanbanAcceptanceTests.P4b_AT4_factory_work_starts_with_no_shortcut_token_and_works_a_kanban_item`: the real `factory work`
host (`FactoryHost.BuildWork`, in process) with KanbanBoard as its only work source, against a throwaway ledger and work root.
The test's options hide the keychain's Shortcut token and blank the Shortcut watch scope and `GitHub:Watch:Repos`, so nothing
of Shortcut is configured; it skips if the environment itself carries `SHORTCUT_API_TOKEN` (unset it for this test).

Prerequisites: as P4b-AT1 (the router, both GitHub Apps, Claude Code for the worker user, `Kanban:WriteToken`,
`FACTORY_E2E_P4B_KANBAN_PROJECT`), no Shortcut token, and

- `FACTORY_E2E_P4B_WORK_KANBAN_ITEM`: the id (GUID) of an item of that project in `Ready`, unclaimed, with no open blocker,
  describing a small change to the sandbox. It must be the project's ONLY `Ready` item: the host claims every ready item it
  sees. The test leaves it in `Backlog` (stopped), or `Done` if the gate merged it first.

```sh
env -u SHORTCUT_API_TOKEN FACTORY_E2E=1 FACTORY_E2E_P4B_KANBAN_PROJECT=<KEY> FACTORY_E2E_P4B_WORK_KANBAN_ITEM=<GUID> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4b_AT4*"
```

What it checks: the options find no Shortcut token; `factory work`'s start-up check (`FactoryRunner.CheckWorkStartAsync`, what
`Program` runs) passes with the Kanban source as the only configured one, its watched project validated on the real board; the
host starts and its intake loop lists the item (its `kb-<n>` is given then — no `factory kanban key`), claims it under the
marker `dark-factory:<item GUID>` and works it to Review, with one `claimed` and one `linked` row, its pull request opened on
GitHub from `factory/kb-<n>`, and the pull request and the branch `pr` and `branch` links on the item (sc-25992: "works a
Kanban item" is asserted as claimed and a pull request opened). Then `factory stop --item kb-<n>` (`FactoryRunner.ControlAsync`, the CLI's code path) while the host runs: the
item's ledger state becomes Cancelled, the board item is back in `Backlog` with no claim, and the pull request is a draft. If
the gate merges the item before the stop lands, the stop is printed as not checked rather than failed.

Not live (every `dotnet test`): `WorkSourceRegistryTests` — the registry (one kind per naming; the configured set for every
combination of sources; a watched source without its credential refused; an item by id needing only its source's credential),
each wiring point (a lane per source that is watched or has its credential, and only a watched issues lane prepared; a source
with its credential and no watch scope asked nothing — no listing, no scope check, nothing claimed — while a poll still finishes
the pending Stop of its in-flight item; the start-up warning per source with active ledger items and no credential; one poll
running a Shortcut story and a Kanban item; the in-flight listing per source; Stop by id of a Kanban item on the fake board — pull request drafted, `Backlog`,
claim released, comment, Cancelled — with a same-numbered Shortcut story untouched; a factory-scope Stop over both sources and
an item whose source is not configured reported as not stopped; Stop of one item whose source has no credential refused
before any control is written; Pause and Continue by id; the epic scope asking each item's own
board), the start-up check (KanbanBoard alone with no Shortcut token; Shortcut alone asking nothing of KanbanBoard; no source →
the refusal naming every source's settings and how to finish leftover work by id; a bad scope or missing credential of either source), `factory run` exiting 2
before the router is asked when the item's source is not configured, the work host built with KanbanBoard alone, the help
text, and a scan that no file outside `Shortcut/` but the registry names the Shortcut source's type. These tests build their
options with no keychain and no process environment, so they run (never skip) on a machine whose shell carries
`SHORTCUT_API_TOKEN` or `FACTORY_KANBAN_WRITE_TOKEN`.

## P4b-AT5 — two at once: two items in a worker session at the same time, each in its own slot, both merged (sc-25989)

`SlotAcceptanceTests.P4b_AT5_two_items_are_in_a_worker_session_at_the_same_time_each_in_its_own_slot_and_both_merge`: the real
`factory work` as a child process (the built CLI) with `Worker:Slots=2` and `Intake:MaxConcurrentRuns=2`, against a throwaway
ledger and work root. Written with sc-25989 and run by the epic's terminal story; it skips unless two slots are configured.

Prerequisites: the shared harness's (the router and an enrolled plan, both GitHub Apps on the target repository with its
rulesets, the Shortcut token and watch scope: `FACTORY_E2E_WATCH_TEAM` and/or `FACTORY_E2E_WATCH_EPIC`), and

- a machine set up for two worker slots — `sudo scripts/setup-worker-user.sh --slots 2` (it creates `_factory2` with its own
  helper, sudoers line, home and Claude Code; a run kills every process of both sandbox users) — and `Worker__Slots=2` in the
  environment the test runs in (the harness then holds both slots' locks of the real work root and probes both helpers);
- `FACTORY_E2E_P4B_SLOT_STORIES`: two To Do stories in the watch scope, `sc-<a>,sc-<b>`, each a small fix to the sandbox
  repository that takes the worker a few minutes and that the gate can merge (a new test that fails on the base). They must be
  the only ready stories of the scope: the host claims every one it sees. Both end `Done`, merged.

```sh
FACTORY_E2E=1 Worker__Slots=2 FACTORY_E2E_WATCH_EPIC=<id> FACTORY_E2E_P4B_SLOT_STORIES=sc-<a>,sc-<b> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4b_AT5*"
```

What it checks, from the throwaway ledger and GitHub: both items reach Merge with no Escalated or Cancelled row; each has
exactly one `slot` row and the two differ (`1 _factory` and `2 _factory2`); a `worker_sessions` row of one item overlaps in
time one of the other (two worker sessions at once); each item has one pull request for its branch, merged. INCONCLUSIVE (the
overlap assertion fails, listing every session's times) if the two fixes are so quick that the sessions never overlap: use
stories whose fix takes minutes.

## P4b-AT6 — slot affinity: a run killed during Implement resumes in the same slot while another item runs in the other (sc-25989)

`SlotAcceptanceTests.P4b_AT6_a_run_killed_during_implement_resumes_in_the_same_slot_while_another_item_runs_in_the_other`: two
`factory run sc-N --ignore-scope` child processes (the built CLI), one item each, with `Worker:Slots=2`, against a throwaway
ledger and work root. Once both ledgers name a Claude session — both items are in a worker session, in different slots — the
test SIGKILLs the first child (its own pid only, `OwnProcess`; never a process tree, never a sandbox user's process) and starts
it again. Written with sc-25989 and run by the epic's terminal story.

Prerequisites: as P4b-AT5 (two slots set up, `Worker__Slots=2`; no watch scope is needed), and

- `FACTORY_E2E_P4B_AFFINITY_STORIES`: two To Do stories, `sc-<killed>,sc-<other>`, each a fix that takes the worker several
  minutes (the second must still be in its session, or have finished it by itself, when the first is killed and resumed).
  Both end In Progress with an open pull request (the test stops at Review), or `Done` if the gate merges them.

```sh
FACTORY_E2E=1 Worker__Slots=2 FACTORY_E2E_P4B_AFFINITY_STORIES=sc-<killed>,sc-<other> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4b_AT6*"
```

What it checks. The killed item: every `session` row before and after the kill names the same Claude session, its
`worker_sessions` rows hold that one session id, and it has exactly one `slot` row, the same before and after — the resumed
run waited for (or found free) the slot that started it and resumed there, stopping the first run's leftover worker through
that slot's helper only. The other item, in the other slot: its worker was still running or had finished by itself after the
kill and the resume; it has one `worker-started` row, one session id, one `slot` row (the other slot), no `orphan-killed`, no
`worktree-lost`, no Paused or Escalated row, and it reaches Review. Each item has exactly one pull request for its branch.

Not live (every `dotnet test`): `SlotPoolTests`, `LockScopeTests`, `WorkerSandboxTests` (the helper only through `SafeHelper`),
`SetupScriptTests` and `FactoryOptionsTests` — the pool, the slot recorded per item and kept (resume, the stop of a leftover
worker, the worktree's deletion, each waiting for the item's slot), each slot's helper sweeping only its own user's processes,
start-up naming the slot whose installed helper is missing, stale or pinned to another user, the set-up script for n users, and
the setting's validation. The live sandbox probes (`LiveWorkerSandboxTests`, `FACTORY_SANDBOX_LIVE=1`) probe every configured
slot's user (AT6 above: `Worker__Slots=2` for two), including that each slot's `TMPDIR` is its own user's.

## P4b-AT7 — defaults: one live test per earlier phase behaves as before (sc-25992)

The epic's P4b-AT7: "With default settings the Phase 1–4a live tests behave as before (one of each phase re-run)." Run each of
the four below on a machine whose settings are Phase 4b's defaults — no `Kanban:Watch:Projects`, no `Kanban:WebUrl`, no Kanban
write token in config or the environment (`Kanban:WriteToken`, `FACTORY_KANBAN_WRITE_TOKEN`: with one, the Kanban source gets an
intake lane; one in the keychain, account `kanban-write-token`, is hidden from the run's options by the harness, so it need not
be removed), `Worker:Slots` unset (1) and `Intake:MaxConcurrentRuns` unset (1). `Kanban:Token` and `Kanban:McpUrl` are
Phase 3's (the reviewers' Kanban reads) and may stay. The machine must be set up for Phase 4b first (`sudo
scripts/setup-worker-user.sh`, "Upgrading to Phase 4b" below), or every test fails at start-up with "Stale helper".

`FACTORY_E2E_P4B_DEFAULTS=1` marks the run as AT7: the shared harness (`E2e.StartAsync` and every `E2e.Options`) then FAILS the
test before it starts if any of the settings above is not the default (`E2e.DefaultsProblems`; unit-tested, not live, by
`DefaultsGuardTests` in every `dotnet test`), so an AT7 run cannot pass with Kanban or slots configured. Each test needs its own
fresh story, as in its own section.

| Phase | Test | Section |
| --- | --- | --- |
| 1 | `FactoryWorkTests.Factory_work_takes_a_ready_story_to_review_with_links_ledger_replay_and_cost` | AT2 |
| 2 | `ReviewGateTests.Gate_merges_a_green_pr_a_high_class_panel_passed_and_the_ledger_records_the_merge_commit` | P2-AT1 |
| 3 | `CodeGraphReviewTests.A_review_reads_the_code_and_asks_codegraph_and_the_verdict_and_pr_body_record_it` | Phase 3 (P3-AT1) |
| 4a | `PlanAcceptanceTests.A_plan_item_is_planned_by_a_real_planner_into_stories_released_in_dependency_order_merged_into_its_changeset_branch_and_then_into_the_default_branch` | P4-AT1 |

```sh
# Every Phase 4b setting unset in this shell (the keychain's write token is hidden by the harness under FACTORY_E2E_P4B_DEFAULTS=1):
unset Kanban__Watch__Projects Kanban__WebUrl Kanban__WriteToken FACTORY_KANBAN_WRITE_TOKEN Worker__Slots Intake__MaxConcurrentRuns
export FACTORY_E2E=1 FACTORY_E2E_P4B_DEFAULTS=1
FACTORY_E2E_WATCH_EPIC=<test epic id> FACTORY_E2E_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*Factory_work_takes_a_ready_story_to_review*"
FACTORY_E2E_GATE_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*Gate_merges_a_green_pr_a_high_class_panel_passed*"
FACTORY_E2E_P3_REVIEW_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*A_review_reads_the_code_and_asks_codegraph*"
FACTORY_E2E_P4_PLAN_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*A_plan_item_is_planned_by_a_real_planner*"
```

(Each test's further prerequisites — the watch scope, the gate App, CodeGraph — are in its own section.)
Pass: each test passes as it did before Phase 4b, and the summary shows four passed, none skipped.

### Skipped is not passed

Every live test SKIPS — never fails — when its opt-in (`FACTORY_E2E=1`), a credential or one of its variables is missing
(`Harness.RequireOptIn`/`RequireSecret`/`RequireEnv`, the convention since Phase 1), so `dotnet test` stays green on a machine
with none of them. The Phase 4b tests keep that convention. An acceptance run therefore reads the summary: a test reported
skipped did not run, whatever the exit code. The only failures before a test starts are the ones above that make a run
meaningless (the AT7 guard) or that the harness already had.

## Upgrading to Phase 4b (sc-25980)

`sudo scripts/setup-worker-user.sh` once after pulling Phase 4b: the launch helper is version 5 (version 4 added the slots; version 5, sc-25992, gives each sandbox user its own `TMPDIR`: in the shared `/tmp`, `_factory`'s NuGet lock directory refused `_factory2`'s `dotnet restore`, found by the live P4b-AT5), and until it is re-installed
every `factory run` and `factory work` fails start-up with "Stale helper … re-run `sudo scripts/setup-worker-user.sh`" (close
any `_factory` session and stop `factory work` first: a setup run kills every `_factory` process). With default settings (no
Kanban settings, `Worker:Slots` and `Intake:MaxConcurrentRuns` unset) Phase 4b differs from `main` before it only in these,
each deliberate:

- `factory work` with nothing watched (no Shortcut scope, no `Kanban:Watch:Projects`, no `GitHub:Watch:Repos`) exits 2 naming
  the three settings; before, it started with a warning and picked nothing up (owner decision, confirmed 2026-10-14: kept).
- `factory run` beside a running `factory work` waits at its worker phases for the sandbox user (`[lock] … waiting`), instead of
  failing at start because the work root's lock was held.
- The ledger gets `slot-wait`, `slot-held` and `slot-released` rows (outcome `passed`) and the dashboard a Run column, also on a
  one-slot install.
- The help, the start-up and the readiness checks run before the router check, so a bad setting or a stale helper exits 2
  without a router call.
- The GitHub-issues intake lane is present whenever the workers' GitHub App is configured (it triages only watched
  repositories), so in-flight issue items are finished even with `GitHub:Watch:Repos` cleared.
- A ready item is not dispatched when the factory was paused, paused for usage or frozen after the poll began, and the
  pipeline page's deferral list says what holds new work.
- MCP session files are named `<owner>.<guid>.json` (the owner is the item whose run opened the session).
- `factory work` polls on its interval while runs are in flight (sc-25992): every poll renews claims, and lists and starts work
  only into the run capacity left free; with `Intake:MaxConcurrentRuns` 1 a poll during a run starts no ordinary item, and the
  run order is unchanged. When a run ends after such a poll the next poll comes at once (as the old loop's did), not at the next
  interval.
- So during a long run every `Intake:PollSeconds` also brings the poll's reads, where the old loop made none until the run
  ended: claim renewals, the router's usage read and the freeze evaluator — its main-red check's GitHub reads (each repository's
  base tip and CI), cached 30 s as before.
- An item being stopped and a plan item waiting on its stories are started every poll whatever runs are in flight (outside
  `Intake:MaxConcurrentRuns`; their sandbox work still takes a worker slot), so a pending Stop or a plan's releases no longer
  wait for another item's run to end.
- The GitHub issues triage runs in the background of the loop (one at a time), beside item runs; its end lists what it
  released at once. A triage waiting for the sandbox user no longer delays the next poll.

## Phase 4b — Kanban contract fixtures: recording against the hosted board (sc-25986)

`KanbanContractTests` replay `tests/DarkFactory.Orchestrator.Tests/Fixtures/kanban/*.json` in every `dotnet test`: the Kanban
work source's whole lifecycle, each `tools/call` matched strictly by tool name and arguments. **The committed fixtures are
hosted recordings** (sc-25992, 2026-10-14): each says `"recorded_from": "hosted KanbanBoard"`, and CI sets
`KANBAN_FIXTURES_REQUIRE_HOSTED=1`, so a provisional one (`"in-repo fake (provisional; …)"`, what `KANBAN_RECORD=fake` writes)
fails the build. The first hosted recording found that the scrubbing of a refusal's message also rewrote claim markers
(the factory's identity is called `factory`, so `dark-factory:<id>` became `dark-fixture-recorder:<id>`): a name is now
scrubbed, and guarded, only where it stands on its own (`KanbanIdentities.Mention`).

Prerequisites:

- a KanbanBoard personal access token of scope `full` — the factory's service identity will do, or a throwaway identity of
  its own (KanbanBoard's `docs/deployment.md`, "Factory service identity"). It needs to create and delete a project;
- nothing else: no ledger, no Postgres, no GitHub, no model, no `FACTORY_E2E`.

Run (the token is read from the environment of this one command and from nowhere else — not the keychain, not user-secrets,
not a file; it is sent as the bearer header and is never written to a fixture or printed):

```sh
dotnet build
KANBAN_RECORD=1 KANBAN_RECORD_TOKEN="$(pbpaste)" \
  tests/DarkFactory.Orchestrator.Tests/bin/Debug/net10.0/DarkFactory.Orchestrator.Tests -method "*KanbanFixtureRecording.Record"
```

| Variable | Meaning |
| --- | --- |
| `KANBAN_RECORD` | `1`: record against a real KanbanBoard. `fake`: record against the in-repo fake (how the provisional fixtures were made). Unset: the recorder is skipped |
| `KANBAN_RECORD_TOKEN` | the `full`-scope token; required with `KANBAN_RECORD=1`, the environment only |
| `KANBAN_RECORD_URL` | the MCP endpoint; default the hosted one (`Kanban:McpUrl`'s default). It must be an absolute `https` URL — the full-scope token is sent to it — or `http` on a loopback host (`localhost`, `127.0.0.1`, `[::1]`: a KanbanBoard on this machine); anything else is refused before any request |
| `KANBAN_FIXTURES_REQUIRE_HOSTED` | `1`: `dotnet test` fails while any committed fixture is still provisional (`KanbanContractTests.The_committed_fixtures_say_where_they_were_recorded_…`). Set it for the run that checks the re-recording, and in CI once the hosted recordings are committed |

What it creates, all its own: one project named `[dark-factory fixture] contract recording` with the key `DFFIX<8 hex>`
(created under the idempotency key `dark-factory:fixture:<run id>`, `repository_url` the sandbox repository), in it one epic
(`[dark-factory fixture] epic`), one epic document and sixteen work items titled `[dark-factory fixture] …` — the
scenarios' items and the two a plan creates. It claims, moves, comments on, links and makes dependencies between those items
only. Besides that it reads `list_projects` (every project is listed; only its own enters a fixture) and writes nothing
outside its project.

What it leaves: nothing but archived or deleted throwaways. When the scenarios are done — or one failed, or the run was
cancelled (Ctrl-C, a test timeout: the clean-up runs on a token of its own, two Kanban timeouts long) — it deletes the
project (`delete_project` takes the epic, the document and every item). That is the only hard delete the recorder ever sends,
and it names the id the board returned to this run for the project this run created. If the board refuses the delete it
archives the project instead (`update_project is_archived: true`). Then it reads the board again and fails the run, naming
what is left (project id and key), unless its project is deleted or archived and every item it made is gone or inside its
archived project; a delete whose answer was lost after it happened is found deleted by that read and is not a failure. If
the clean-up itself fails (the board cannot be reached), the run fails saying the project may still be there, with its key
and id.

What an earlier run that died left is never deleted by a later run — that run did not create it. Each run starts by listing
the projects (archived ones too) whose name starts `[dark-factory fixture]` AND whose key starts `DFFIX`, and looks into
those only:

- one that is exactly a recording's — the name is `[dark-factory fixture] contract recording`, the key is `DFFIX` and 8
  upper-case hex digits, and every work item (closed ones and those of archived epics included), every epic and every epic
  document in it is titled `[dark-factory fixture] …` — is archived if it is not yet (`update_project is_archived: true`,
  reversible: the only write of this step) and printed with its key and id, as a `[kanban fixtures] …` line on standard
  error and in the test's output, every run until it is gone. Delete it by hand in the web UI when you have looked at it;
- one that is not exactly that (another name or key shape, or anything inside it that is not a throwaway) is someone's: it
  is left untouched, nothing at all is written, and the run fails before it creates anything, naming the project (key, id,
  and what is off — counted, not quoted). Rename or remove it by hand, then run again.

KanbanBoard's contract gives a project no creator or owner, so "created by this token" cannot be part of that test; that is
why a leftover is only ever archived.

The fixtures are written only after the board was cleaned up and checked, and only after every check on them passed: the
literal token is in none of them (searched for as text, whatever its shape), the guards find nothing (below; with the names
and ids of the identities this run met), and each replays strictly in every framing. The seven files are then replaced
together — written to a staging folder beside `Fixtures/kanban`, read back, and swapped in — so a failed run leaves the
committed set as it was.

Identities: the token's identity is recorded as the name `fixture-recorder` and the id
`f0000000-0000-0000-0000-000000000001` (each further id met gets the next number; the same id is always the same
placeholder) in `claimant`/`claimant_id`, `author`/`author_id` and a change's `actor`; its real name and id, learnt from the
recorder's own first claim, are also replaced in every refusal's `message`. An e-mail address in any text becomes
`redacted@example.com`. The contract's other shapes carry no identity (no creator, owner, e-mail or avatar field), and the
change feed (`list_changes`), which the adapter does not read, is refused rather than recorded.

`busy` and `conflict`: the `conflict` in `conflict.json` is the board's own — the recorder edits the item (as another
caller would) between the adapter's read and its conditional move, so the board refuses the stale `expected_version`. A
`busy` cannot be provoked on demand through the contract (the board answers it only after a write waited 10 s for other
writers), so the first `update_work_item` of `busy.json` is authored from the contract's Error shape by the recording
handler and not forwarded; it carries a `note` saying so, and the adapter's resend after it is the board's own answer.

Verify afterwards:

1. the command printed `Failed: 0, Skipped: 0`, and no `[kanban fixtures] …` line (one names a leftover project of an earlier
   run, archived and not deleted: look at it on the board and delete it by hand);
2. `git diff --stat tests/DarkFactory.Orchestrator.Tests/Fixtures/kanban` shows all seven files, and
   `grep -L '"recorded_from": "hosted KanbanBoard"' tests/DarkFactory.Orchestrator.Tests/Fixtures/kanban/*.json` prints nothing;
3. `dotnet build && KANBAN_FIXTURES_REQUIRE_HOSTED=1 dotnet test` passes: the adapter replays the hosted recordings, and the
   guard tests find no token, e-mail address, identity's name or id, or anything that is not a `[dark-factory fixture]`
   throwaway of the recording's own project (read the diff before committing all the same — the repository is public; in
   particular `grep -h '"message"' tests/DarkFactory.Orchestrator.Tests/Fixtures/kanban/*.json`: a refusal's message is the
   board's free text, and the guard can only know the names the run itself met);
4. on the board (web UI, archived projects shown): no project named `[dark-factory fixture] …`, or only archived ones.

If the replay fails on a hosted recording, the adapter and the hosted server disagree about the contract: that is the finding
the recording exists for — fix the adapter (or KanbanBoard), never the fixture by hand.

Not live (every `dotnet test`): `KanbanContractTests` — the replay of each fixture in each framing, what each recording holds,
strict matching (a tool or argument the recording does not have, a missing or changed one, an extra call, a call never made),
the handshake checks, normalisation of both framings, trimming, the scrubbing of names, ids and e-mail addresses (on the
answer's strings, never its JSON text; one set of identities for the whole run), the guards on mutated fixtures (a token in
six shapes, an e-mail, a foreign item, project, epic, document, blocker, listing entry, an identity's name, an id that is not
the placeholder, a refusal message naming an identity, an over-long listing), and the provisional check — and
`KanbanFixtureRecording`: the recorder end to end against the fake board (its fixtures pass every check `Record` makes before
saving; someone else's project on the same board is neither recorded nor written; the board is left as found), a scenario
that fails and a run that is cancelled (the project is still removed), a clean-up that itself fails (the run names the
project), a delete the board refuses (archived; left archived and named by the next run), a delete whose answer is lost (not
a leftover), a project that can be neither deleted nor archived (the run fails naming it), a project whose create answer
never arrived (reported; archived by the next run), the sweep (an exact leftover archived and named, nothing deleted; seven
kinds of look-alike left untouched and failing the run with nothing written), the recording URL rule, the pre-save checks
(the literal token, a replay, the whole set, the guards) and the all-or-none save.

## Upgrading a Phase 1 ledger: items parked at Review

Phase 1 parked every finished item at Review; since sc-25378 Review is a state the factory drives, so the first
`factory work` (or `factory run sc-<id>`) after the upgrade picks up every such item. One whose PR is still open is
reviewed by the panel like any other (Phase 1 recorded no `implementer-model`, which no longer matters: since sc-25626 the
panel runs on the router's high model class whichever models implemented it). One whose PR the owner already handled escalates once, with a
comment on its story:

- "… is merged outside the factory." / "… is closed outside the factory.": the owner already merged or closed the PR.
  Nothing more is needed from the factory: move the story on the board by hand. The item stays Escalated (in-flight
  pickup never resumes an Escalated item).

Expected and one-time: these escalations are the ledger catching up, not failures of the gate.

An item already past Review (at CI or MergeGate) whose head has a passing verdict recorded before sc-25626 — by pinned
reviewer models, with no served model class — is not escalated for it: the gate sends it back to Review and the current
panel (high class) reviews that head once, then it merges as usual. This happens once per head: if the new verdict's calls
still break the rule (e.g. the router answers without an `X-Weave-Model-Class: high` header), the gate escalates with the
reason rather than reviewing again.

# Phase 5 acceptance runbook (Swift, epic sc-26124; sc-26134)

Phase 5 makes the factory work Swift: SwiftPM packages (Swift Testing and XCTest), macOS Xcode projects and iOS apps, whose
unit and UI tests the gate's new-tests check runs on a fresh iOS Simulator as the sandbox user. The live tests are
`SwiftAcceptanceTests` (P5-AT1–AT6) and `SwiftRealRunTests` (P5-AT7) in `tests/DarkFactory.AcceptanceTests`, with their helpers
in `SwiftLive.cs`. Like every live test since Phase 1 each one **skips** — never fails — without `FACTORY_E2E=1`, a credential
or one of its own variables, so `dotnet test` stays green and runs none of them; **skipped is not passed** (read the summary).
Each of P5-AT1–AT6 runs `factory run` (the production wiring, `FactoryRunner.RunAsync`) on a To Do story against a **throwaway
ledger** and its own work root (`E2e`), and takes every worker slot's lock of the configured work root for its whole run: stop
`factory work` first, and run the tests **one at a time** (a second test that finds the locks taken skips).

**Read before running anything — order and preconditions:**

- **P5-AT5's first part (`P5_AT5_the_gate_refuses_…`) must be run live BEFORE CodeGraph C3 (sc-26137) is deployed, and its
  second part (`P5_AT5_the_gate_accepts_…`) AFTER.** The refusal of an unindexed Swift change can only be shown while CodeGraph
  does not index Swift (the first part skips once it does), and the acceptance only once it does. Missing the window before C3
  means the refusal is never shown live.
- **P5-AT1–AT4 and P5-AT5's second part skip until C3 indexes Swift** (prerequisite 6). Skipped is not passed: they are run
  again after C3.
- **P5-AT3 and P5-AT4 one at a time, never side by side**: both raise the one run counter of the iOS app
  (`e2e-ios/Tally/E2eRun.swift`) from main's value, so two runs at once would raise it to the same number and the later merge
  would conflict or its test prove nothing.
- **Every test from the owner's console login** (an Aqua session: Terminal on the Mac's own screen, not ssh), so the iOS tests can
  reach the simulator (prerequisite 3).

## Prerequisites (the owner, once)

1. **The router**: simple-router at `http://127.0.0.1:8790` (the default `Router:BaseUrl`), its key in the keychain (account
   `router-key`), Claude (and Codex) enrolled (`simple-router login claude`), its `max`/`high`/`mid`/`low` tiers servable.
2. **The sandbox users with Swift**: `sudo scripts/setup-worker-user.sh --slots 2 --swift` (stop `factory work` and close every
   `_factory*` session first: setup kills every process of each sandbox user), then in the shell that runs the tests
   `export Worker__Swift=true Worker__Slots=2`. Every test checks `Worker:Swift` and skips without it: the Swift and Xcode
   strategies run only in a factory with it on. Each slot's user needs Swift, xcodebuild with Xcode's first launch done and its
   licence accepted, the macOS and iOS Simulator SDKs, an iOS Simulator runtime, and the installed `factory-xcodebuild`
   (`CLAUDE.md`, "Swift toolchain", says which of these setup and start-up check).
3. **For iOS (P5-AT3, P5-AT4, P5-AT7's iOS change): the owner logged in at the console** (an Aqua session), and the tests (or
   `factory work`) started from that GUI login — Terminal, not ssh. The simulator works as the sandbox user only inside the
   owner's Aqua session (`CLAUDE.md`, the S0 residual); from ssh the gate's `prepare` step refuses, as the factory's failure, not
   the item's.
4. **Both GitHub Apps** (the workers' and the gate's) installed on every repository below, `factory github-repo protect
   <owner/name>` run for each (the gate App as the `pull_request`-mode bypass), and each repository named in `Factory__Repos`
   (e.g. `export Factory__Repos=michaeltrefry/LensWorks`) so the factory may plan and work in it and the freeze's main-red
   watches it.
5. **LensWorks** (`michaeltrefry/LensWorks`, default branch `main`; the macOS app `native/Compositor.xcodeproj`, scheme
   `Compositor`, Swift Testing unit target `CompositorTests`): merge the setup pull request from LensWorks branch
   `claude/dark-factory-setup` — a **version 4** `factory/gate.yaml` (with `max_changeset_lines`/`max_changeset_files`),
   `CODEOWNERS` covering the sealed paths, and a CI workflow that runs the unit tests on pull requests (its `pull_request`
   trigger must also match `factory/**` bases if LensWorks plans are run: "CI that cannot run" in `CLAUDE.md`). The tests read
   the policy from `main` through the gate App and skip naming the step when it is missing.
6. **CodeGraph**: the token (`FACTORY_CODEGRAPH_TOKEN` or keychain `codegraph-token`), and every target repository (the sandbox,
   LensWorks, the iOS repository) indexed. **Until CodeGraph C3 (sc-26137) is deployed and indexes Swift, the gate blocks every
   Swift change on `risk-threshold`** (`CodeGraph does not index the language of a changed code file …`; the gate row's inputs say
   `language not indexed by CodeGraph`; sc-26133: fail closed). So **P5-AT1–AT4 and P5-AT5's second part can only pass after
   C3**: each asks CodeGraph first about a Swift file of its repository on `main` (`SwiftLive.LanguageIndexedAsync`: `file_hotspot`,
   then `analyze_impact_file` at the default-branch index commit that answer names, decided by the gate's own rule over both
   answers, `LanguageCoverage.Of`; no flag at all means a CodeGraph before C3) and SKIPS saying so while Swift is not indexed.
   P5-AT5's first part is the opposite and skips once Swift is indexed. The probe FAILS — it never skips — when CodeGraph answers
   either tool with an error, or counts the language as indexed while it has no data for the file (`found` not true): a probe
   that read either as "not indexed" would leave the merging tests skipping for ever and the refusal test running on a false
   premise. P5-AT6 does not depend on it (the gate rejects the test
   before it asks CodeGraph).
7. **The seeds** (once, by an owner's pull request to each repository's `main`): the tests seed only a test, and the production
   change it needs, into a target that already exists on the base — a test target the base lacks cannot be run there (the check
   would be `unsupported`; `SwiftLiveTests.Without_the_package_on_the_base_the_merging_change_could_not_be_run_there`). Copy:
   - `tests/DarkFactory.AcceptanceTests/Seeds/swift-package/` to **`e2e-swift/`** of the sandbox
     (`michaeltrefry/dark-factory-sandbox`): the SwiftPM package `DfE2eSwift`, test target `DfE2eSwiftTests` (XCTest), for
     P5-AT2, AT5 and AT6;
   - `tests/DarkFactory.AcceptanceTests/Seeds/ios-app/` to **`e2e-ios/`** of the iOS repository, for P5-AT3 and AT4: the iOS
     app `Tally` (`SDKROOT = iphoneos`, synchronized folders; the unit tests' fixture app, `Fixtures/xcode-ios`), its hosted
     unit-test target `TallyTests`, its UI-test target `TallyUITests`, a shared scheme, and `Tally/E2eRun.swift` — the run
     counter (`static let count = 0`) the app shows as the `e2e-runs` label.

   The iOS repository is `FACTORY_E2E_P5_IOS_REPO` (`owner/name`), **default: the sandbox**: the owner has no iOS app on GitHub
   today (no SceneWorks iOS app exists), so the throwaway `Tally` app in the sandbox stands in; `FACTORY_E2E_P5_IOS_DIR` moves
   the seed (default `e2e-ios`). Every seeded file says `[dark-factory e2e]`; the sandbox's own .NET CI and build read none of
   them. A test whose seed is missing on `main` skips naming this step.

## P5-AT1 to P5-AT6

Every `*_STORY` is a **fresh To Do story**, one per test. For the sandbox ones: a bug story whose fix lands in **normal** code of
the sandbox (the P2-AT1 kind), with every new xUnit test it asks for failing on main — the merging tests need the story's own
tests to pass the check too, and a rejection names only the first failing strategy (the P4-AT4 lesson) — except P5-AT6's,
which must tell its implementer to add **no** .NET/xUnit test (below). The seeded tests push
their Swift files to the item's `factory/sc-<id>` branch as the workers' App before the first review, and the review panel is the
seeded stand-in `SeededReviewer`, so the gate's own rule decides; P5-AT1 is a real implementer and the real panel.

```sh
export FACTORY_E2E=1 Worker__Swift=true Worker__Slots=2 Factory__Repos=michaeltrefry/LensWorks
FACTORY_E2E_P5_LENSWORKS_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P5_AT1_*"
FACTORY_E2E_P5_SWIFTPM_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P5_AT2_*"
FACTORY_E2E_P5_IOS_UNIT_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P5_AT3_*"
FACTORY_E2E_P5_IOS_UI_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P5_AT4_*"
FACTORY_E2E_P5_UNINDEXED_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P5_AT5_the_gate_refuses_*"
FACTORY_E2E_P5_INDEXED_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P5_AT5_the_gate_accepts_*"
FACTORY_E2E_P5_SWIFT_PASSING_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P5_AT6_*"
```

(Add `FACTORY_E2E_P5_IOS_REPO=<owner/name>` and `FACTORY_E2E_P5_IOS_DIR=<dir>` to AT3/AT4 for another iOS repository; its story
then needs a `Repo:` line naming it.)

| AT | Test (`SwiftAcceptanceTests.`) | Variables | Setup it does | Expected |
| --- | --- | --- | --- | --- |
| P5-AT1 | `P5_AT1_a_lensworks_change_with_a_new_swift_testing_test_merges_with_no_human` | `FACTORY_E2E_P5_LENSWORKS_STORY`: a To Do story with a `Repo: michaeltrefry/LensWorks` line asking for a small change to the Compositor app and a new Swift Testing (`@Test`) test of it in `CompositorTests` that fails without the change (the test may call API the change adds: it is then `not-built` on the base) | none: a real implementer, the real high-class panel, LensWorks' CI | the item's ledger repo is LensWorks; a typed outcome on every row; the last `new-tests` row `pass`, its strategies holding `xcode-swift-testing`, with a `CompositorTests/…` test not passing on the base and `passed` on the head; the last `gate` row a `Merge` decision (`passed`) listing `new-tests-fail-on-base` and naming no unindexed file; `gate-passed` = the head the check judged; MergeGate → Merge → Watch; the last verdict passed, on the high class, for that head; the PR merged at that head, its merge commit the Merge row's; `CompositorTests`, read from the merge commit's project, a macOS unit-test bundle; the story Done |
| P5-AT2 | `P5_AT2_a_swiftpm_change_with_a_new_xctest_test_merges` | `FACTORY_E2E_P5_SWIFTPM_STORY` | pushes `e2e-swift/Sources/DfE2eSwift/P<run>.swift` (a new function) and `e2e-swift/Tests/DfE2eSwiftTests/P<run>Tests.swift` (an XCTest test calling it) | AT1's ledger checks with strategy `swift-testing`; `DfE2eSwiftTests.P<run>Tests/testDoubles` `not-built` on the base (it calls code the PR adds) and `passed` on the head; merged; **the sandbox's main then holds both files** (one pair per run, safe to delete) |
| P5-AT3 | `P5_AT3_an_ios_app_change_with_a_new_unit_test_run_in_the_simulator_merges` | `FACTORY_E2E_P5_IOS_UNIT_STORY`; optional `FACTORY_E2E_P5_IOS_REPO`, `FACTORY_E2E_P5_IOS_DIR` | raises `e2e-ios/Tally/E2eRun.swift`'s count to main's + 1 and pushes `e2e-ios/TallyTests/E2eUnit<run>Tests.swift` (XCTest: `E2eRun.count >= <count>`) | strategy `xcode-swift-testing`; `TallyTests/E2eUnit<run>Tests/testRunCountReached()` `failed` on the base and `passed` on the head, each on the run's own fresh iOS Simulator as the sandbox user; merged; its target, read at the merge commit, an **iOS** unit-test bundle; **main's run count goes up by one** |
| P5-AT4 | `P5_AT4_an_ios_ui_test_runs_in_the_simulator_and_merges` | `FACTORY_E2E_P5_IOS_UI_STORY` (+ the same optional two) | raises the count likewise and pushes `e2e-ios/TallyUITests/E2eUi<run>UITests.swift` (XCUITest: launch the app, read the `e2e-runs` label, `>= <count>`) | `TallyUITests/E2eUi<run>UITests/testRunCountShown()` `failed` on the base and `passed` on the head (the S0 way: everything that needs the simulator booted in one helper run, a fresh device per run, deleted after); merged; its target an **iOS UI-test** bundle |
| P5-AT5 (a) | `P5_AT5_the_gate_refuses_a_swift_change_while_codegraph_does_not_index_swift` | `FACTORY_E2E_P5_UNINDEXED_STORY`; skips once CodeGraph indexes Swift | AT2's change | the `new-tests` row `pass` (nothing else blocks it); the last `gate` row `Blocked …CodeGraph does not index the language of a changed code file…` (`gate_rejected`), its inputs `language not indexed by CodeGraph: e2e-swift/Sources/DfE2eSwift/P<run>.swift (Swift…`; MergeGate → Escalated; nothing merged; the PR closed and its branch deleted at the end |
| P5-AT5 (b) | `P5_AT5_the_gate_accepts_the_swift_change_once_codegraph_indexes_swift` | `FACTORY_E2E_P5_INDEXED_STORY`; skips until CodeGraph (C3) answers `languageIndexed: true` for Swift | AT2's change | merged as AT2; the `gate` row's inputs list the Swift source file's blast radius (`codegraph blast radius at …`) and no `language not indexed` |
| P5-AT6 | `P5_AT6_a_new_swift_test_that_already_passes_on_the_base_is_gate_rejected` | `FACTORY_E2E_P5_SWIFT_PASSING_STORY`: a sandbox bug story whose text tells the implementer to add **no** .NET/xUnit test (a rejected result names only the first strategy that rejected; the test fails naming the strategy that did, `SwiftLive.RejectedByProblem`) | pushes `e2e-swift/Tests/DfE2eSwiftTests/E2eAlreadyPassing.swift` (Swift Testing, `#expect(1 + 1 == 2)`) | the last `new-tests` row `rejected` (`gate_rejected`), strategy `swift-testing`, `DfE2eSwiftTests.e2eAlreadyPassesOnTheBase()` `passed` on both and named in the reason; the last `gate` row `Blocked …new-tests-fail-on-base (rejected)…`; MergeGate → Escalated; nothing merged; the PR closed and its branch deleted |

**By hand after the runs:** the non-merging tests leave their story In Progress with an escalation comment (move it back or
archive it); delete a `factory/sc-<id>` branch a crashed run left. The merged files stay on the target's `main` (the sandbox's
`e2e-swift/Sources/DfE2eSwift/P*.swift` and `e2e-swift/Tests/DfE2eSwiftTests/P*Tests.swift`, the iOS repository's
`e2e-ios/TallyTests/E2eUnit*`, `e2e-ios/TallyUITests/E2eUi*` and its raised run count); they are inert, keep passing, and may be
deleted whenever — each run makes new ones, which is what keeps its test new on every base.

Not live, in every `dotnet test` (`SwiftLiveTests`): each seed identified by the real strategy (`SwiftNewTests`, `XcodeNewTests`)
exactly as the live test looks it up, nothing unisolable, and each strategy leaving the other's files alone; the iOS test targets
read as iOS unit-/UI-test bundles; the run counter read and raised; `SwiftLive.MergedProblems` (the merged ledger's checks)
naming each missing part; the gate's own words for an unindexed language; the CodeGraph language probe (`SwiftLive.ProbeOf`: the
gate's rule over both answers, failing on an error answer or an unknown file); P5-AT6's strategy message; and P5-AT7's epic and
iOS rules (`SwiftLive.EpicProblems`, `SwiftLive.IosProblem`).

## P5-AT7 — a real LensWorks epic and a real iOS change through the factory

A runbook step, then a read-only check of the real ledger.

1. Prerequisites as above (C3 deployed and Swift indexed: before it every Swift change escalates at the gate), with the
   factory's real ledger and settings: `Worker__Swift=true`, `Worker__Slots=2`, `Intake__MaxConcurrentRuns=2` if the stories may
   run side by side, `Factory__Repos` naming LensWorks and the iOS repository.
2. One real LensWorks epic: at least two of its stories To Do with a
   `Repo: michaeltrefry/LensWorks` line, each a real change with a new Swift Testing test in `CompositorTests` (or a
   `plan`-labelled story the factory plans into such stories). One real iOS change: a To
   Do story whose `Repo:` line names the iOS repository and asks for a change with a new unit or UI test in an iOS target. Watch
   them: on Shortcut `Shortcut__Watch__Epics=<the epic>,<the iOS story's epic>`; on KanbanBoard `Kanban__Watch__Projects=<key>`
   with the items Ready.
3. From the owner's GUI login (Aqua): `dotnet run --project src/DarkFactory.Orchestrator -- work`, and follow the dashboard until
   every item is in Watch or Done (an escalation is a finding: fix it and `factory run <id>` again).
4. Check the ledger, read only (no write, no lock, no model):

   ```sh
   FACTORY_E2E=1 FACTORY_E2E_P5_IOS_REPO=<owner/name> FACTORY_E2E_P5_AT7_ITEMS=sc-<id>,sc-<id>,… \
     dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P5_AT7_*"
   ```

   `SwiftRealRunTests.P5_AT7_a_real_lensworks_epic_and_a_real_ios_change_went_through_the_factory` reads each listed item
   (`sc-N` or `kb-N`, as the ledger names it) from the configured ledger (`ConnectionStrings:Ledger`): every row's typed outcome;
   a plan item (it has `plan-stories`) Done; every other item merged as P5-AT1's checks say (MergeGate → Merge → Watch/Done,
   `new-tests` `pass` naming `swift-testing` or `xcode-swift-testing` with a new test failing on the base and passing on the head,
   a `Merge` gate decision naming no unindexed file, `gate-passed` the judged head) and its pull request merged on GitHub at the
   Merge row's commit; and, among them, at least one LensWorks item and one item of `FACTORY_E2E_P5_IOS_REPO` whose new test's
   target, read at the merge commit, runs on an iOS Simulator AND that was run and `failed` on the base (not `not-built`: only an
   executed test proves the simulator ran it; `SwiftLive.IosProblem`). The LensWorks items must be a real epic
   (`SwiftLive.EpicProblems`): at least two listed LensWorks items of one epic, or a listed LensWorks plan item with every story
   of its `plan-stories` listed and merged; and every item the ledger holds of each listed LensWorks item's epic must be listed
   and delivered — the test fails naming one that is not. So list every item of the epic and the iOS change.

## Phase 5 — earlier stacks unchanged, and the rest of the evidence

**One .NET and one Rust live test re-run** with `Worker__Swift=true` and `Worker__Slots=2` (E7: a Swift-enabled factory judges
.NET and Rust exactly as before; the Swift strategies leave their files alone):

| Stack | Test | Section |
| --- | --- | --- |
| .NET | `ReviewGateTests.Gate_merges_a_green_pr_a_high_class_panel_passed_and_the_ledger_records_the_merge_commit` (`FACTORY_E2E_GATE_STORY`) | P2-AT1 |
| Rust | `GateNegativeTests.P4_AT4_a_rust_test_that_fails_on_the_base_and_passes_on_the_head_merges` (`FACTORY_E2E_P4_CARGO_MERGE_STORY`) | P4-AT4 |

```sh
export FACTORY_E2E=1 Worker__Swift=true Worker__Slots=2
FACTORY_E2E_GATE_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*Gate_merges_a_green_pr_a_high_class_panel_passed*"
FACTORY_E2E_P4_CARGO_MERGE_STORY=sc-<id> dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-method "*P4_AT4_a_rust_test_that_fails_on_the_base*"
```

Pass: both pass as in their own sections (not skipped). Not with `FACTORY_E2E_P4B_DEFAULTS=1`: that guard requires one slot.

**Unit, CI, lint and gate-coverage evidence** (no live service):

- `dotnet build` (the gateway lint, sc-25390, runs on the orchestrator and on this acceptance project) and `dotnet test` from the
  repository root green, with the ledger Postgres at `localhost:5434` (`docker compose up -d`): the Swift and Xcode strategies'
  tests (`SwiftNewTestsTests`, `SwiftXCTestTests`, `SwiftBaseBuildTests`, `XcodeNewTestsTests`, `XcodeIosTests`,
  `XcodeIosRunnerTests`, `XcodebuildWrapperTests`, `IosSimulatorScriptTests`, `SwiftToolchainTests`), the risk rule
  (`CodeGraphRiskTests`: an unindexed language fails closed), `SetupScriptTests` (`--swift`) and `SwiftLiveTests` above. Those
  that need Xcode run on the owner's Mac; CI's Linux runner skips them.
- CI (`.github/workflows/ci.yml`, job `build-test`) green on the integration branch's head: build (gateway lint), migrations,
  `dotnet test`.
- `GatewayLintTests` (the lint's seeded violations and the clean tree) and `GateCheckCoverageTests` (every gate check has a
  running test that fails with the check disabled) pass in that run.
- The sandbox probes with Swift, as the owner from the GUI login with no factory running (they kill every process of each
  sandbox user): `FACTORY_SANDBOX_LIVE=1 Worker__Swift=true Worker__Slots=2 dotnet test --project
  tests/DarkFactory.Orchestrator.Tests -- --filter-class "*LiveWorkerSandboxTests"`, among them
  `The_ios_gate_runs_a_new_unit_test_and_ui_test_on_a_simulator_as_each_sandbox_user` (the iOS gate on a fresh simulator in
  each slot, nothing of the user left after).
