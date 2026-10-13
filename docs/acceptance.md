# Phase 1 acceptance runbook (epic sc-25171, AT1–AT8)

How to run each epic acceptance test, what is automated and what the owner does by hand. Everything live is
opt-in: the tests skip, naming the missing prerequisite, unless their gate variable is set.

## Safety first (read before any live run)

- **The `_factory` user is single-tenant.** Every exit of the sandbox launch helper kills *every* `_factory`
  process. Do not use an interactive `_factory` session (e.g. its Claude login) while any live test below runs.
- **Stop your own `factory work` / `factory run` first.** The `factory work` tests (AT2–AT5) hold the configured
  work root's run lock (`<Factory:WorkRoot>/.factory-run.lock`) for their whole run and skip if a real sandboxed run
  holds it, so they never share `_factory` with one. They run against a **throwaway ledger database**
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
(default `http://localhost:8080`), keychain entries for the router key, Shortcut token and GitHub App
(`factory github-app setup`), `sudo scripts/setup-worker-user.sh` done once (or `Worker__RunAs=none` to run workers
as yourself, development only), and the model auth: for `Worker:Auth=router-key` (the default) your plans enrolled on
the router for the router key (`router login claude`, `router login codex`; `factory run`/`work` refuse to start
without one) and `_factory`'s own login removed (`sudo scripts/setup-worker-user.sh --remove-worker-login`); for
`Worker:Auth=claude-login` (weaker, violates E5) the `_factory` Claude login instead.

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
| Worker env is exactly the allowlist for the configured `Worker:Auth` (`router-key`: `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`, `ANTHROPIC_CUSTOM_HEADERS`, `DOTNET_CLI_USE_MSBUILD_SERVER`, `HOME`, `MSBUILDDISABLENODEREUSE`, `PATH`; `claude-login`: the same without `ANTHROPIC_AUTH_TOKEN`); `ANTHROPIC_CUSTOM_HEADERS` is two lines, the router key and `x-weave-model-class` (sc-25659) | `LiveWorkerSandboxTests.Live_worker_environment_is_exactly_the_allowlist_for_the_configured_auth_mode` | `FACTORY_SANDBOX_LIVE=1` |
| `router-key`: `_factory` holds no Claude credential (no `~/.claude/.credentials.json`, no `Claude Code-credentials` keychain item) | `LiveWorkerSandboxTests.Worker_user_holds_no_claude_credential_in_router_key_mode` (skips for `claude-login`) | `FACTORY_SANDBOX_LIVE=1` |
| `router-key`: `factory run`/`work` refuse to start without an enrolled plan | `RouterEnrollmentCheckTests` (unit, fake router) | none |
| `~michael`, `.ssh`, `.config/gh`, login keychain unreachable | `LiveWorkerSandboxTests.Worker_is_denied_the_owners_home_ssh_keys_gh_config_and_login_keychain`, `Worker_cannot_read_the_owners_keychain_items` | `FACTORY_SANDBOX_LIVE=1` |

The env probe runs `/usr/bin/env` through the installed helper exactly as `ClaudeWorker` launches a sandboxed worker
(`WorkerSandbox.Start` with `ClaudeWorker.BuildRouterVariables` for the configured router URL and auth mode, a probe
router key, and `GH_TOKEN` set in the test process to show nothing of the owner's leaks). The `FACTORY_SANDBOX_LIVE`
tests kill every `_factory` process: run them only when no `_factory` session is in use:

```sh
FACTORY_SANDBOX_LIVE=1 dotnet test --project tests/DarkFactory.Orchestrator.Tests -- --filter-class "*LiveWorkerSandboxTests"
```

## AT7 — the router accounts for every model call

Automated, three ways: AT2 and AT5 end by checking every `worker_sessions` row of their run; standalone,
`RouterAccountingTests.Router_reports_requests_and_cost_for_every_worker_session_since_the_given_time` checks any
ledger (`FACTORY_E2E_LEDGER`, default the configured one; read-only) for sessions started since `FACTORY_E2E_SINCE`
(ISO 8601). Each session must be recorded: `GET /v1/sessions/{id}/cost` answers 200 with `request_count > 0`, and its
cost is ≥ 0. A cost of 0 is valid: the router may serve turns (or a whole session) on a local model priced at $0, which
still counts toward `request_count`. A 404 or `request_count == 0` means not recorded (yet).

That proves every *session* is costed, not that no call bypassed a session. Owner, by hand, for the AT2 window: in the
router's logs (`docker compose logs router --since <start> --until <end>` from the router checkout) or its telemetry
store, count the model requests in the window and compare with the sum of the sessions' `request_count`; any request
with no Claude Code session id is a call the factory made outside a worker session and fails AT7. The exact log field
or telemetry query that carries the session id is the router's (michaeltrefry/router) and is not pinned here — check
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
the router's `high` model class (`x-weave-model-class: high`, never `x-weave-force-model`) and the router picks the model,
so the router must have a servable `high` class (its `model_tiers`/`model_classes`; a `model_class_unavailable` 503 pauses
the factory for usage). The factory's router key must have no routing-policy caller passthrough and no blind-experiment
passthrough arm: that path 503s `model_class_unavailable` for a class request whose body model (`default`) is outside the
class, which would usage-pause every review in a loop. A call counts only when the router's `X-Weave-Model-Class` response
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
classes (`x-weave-model-class`, michaeltrefry/router "Model tiers and classes") and have a servable model in `mid` and `low`.

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

Automated: `GateNegativeTests.P4_AT4_a_rust_test_that_already_passes_on_the_base_is_gate_rejected_and_one_calling_new_code_does_not_build_there`
(`FACTORY_E2E=1` and `FACTORY_E2E_P4_CARGO_STORY` = a To Do bug story like AT4's). Same harness, seeding and cleanup as the AT4
negatives above: nothing merges and **the sandbox's main is never written**, so the sandbox needs no Rust of its own.

**Prerequisite (once, the owner):** cargo and rustc for the worker user, on the launch helper's fixed PATH — `/opt/homebrew/bin`
or `/usr/local/bin` (`brew install rust`; or rustup installed as `_factory`, with its `cargo` and `rustc` linked into
`/usr/local/bin`: `~_factory/.cargo/bin` itself is not on that PATH). `sudo scripts/setup-worker-user.sh` reports both
(`ok    cargo --version: …`, or `none  cargo --version … not available to _factory`); the helper itself did not change. The first
run also needs the network for `cargo fetch` (the seeded package has no dependency, so nothing is downloaded).

| Setup it does | Expected on the ledger |
| --- | --- |
| before the first review, commits to the PR branch `e2e-rust/Cargo.toml` (package `df-e2e-rust`) and `e2e-rust/src/lib.rs`: `pub fn double`, and an inline `#[cfg(test)] mod tests` with `doubles` (calls `double`) and `already_passes_on_the_base` (asserts `1 + 1 == 2`) | the last `new-tests` row is `rejected` (`gate_rejected`), strategy `rust-cargo`, `base_run` `build-failed`, `base_retry` `ran`, `head_run` `ran`; `df-e2e-rust:lib:tests::doubles` is `not-built` on the base (it calls code the pull request adds: the base gets the package with its test code alone) and `passed` on the head; `df-e2e-rust:lib:tests::already_passes_on_the_base` is `passed` on both and named in the reason; the last `gate` row is `Blocked …new-tests-fail-on-base (rejected)…`; MergeGate → Escalated; nothing merged |

Without cargo for the worker user the `new-tests` row is `error` (`…(toolchain-missing): cargo is not available to the gate's test
runs as _factory…`) and the test fails on its first assertion, quoting that reason. The same seed runs in every `dotnet test` where
cargo is installed (`CargoRunnerTests.With_cargo_a_new_package_is_applied_to_the_base_with_its_test_code_alone`, unsandboxed), with
`With_cargo_a_new_test_failing_on_the_base_passes_and_one_already_passing_there_is_named` (the `pass` outcome),
`…a_new_inline_test_calling_code_the_pull_request_adds_does_not_build_on_the_base…`, `…in_a_workspace_below_the_repository_root…`
and `…a_base_whose_other_tests_do_not_build_is_an_error_not_evidence`.

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
`plan` label whose repository is the sandbox (the default repo, or a `Repo:` line) and whose description is work worth two or
three small stories, each of which can pass the sandbox's gate on its own (e.g. each a documentation change under the policy's
free tier, or a code change with a test that fails on its base) and which together stay inside the policy's `risk` limits — the
changeset pull request is gated as a whole. The sandbox's CI must run for pull requests whose base is `factory/changeset-*` (a
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
of the plan item's Merge row; one `closeout` `posted`, and the plan story's comment says `sc-<id>'s plan is complete: n of its n
stories merged` and `Its changeset merged into \`main\` as \`<merge commit>\``.

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

Not run live yet (2026-10-12): written with sc-25870 and extended with sc-25871, to be run by the owner. The live run is also the
first time the release's Shortcut requests meet the real API: `GET stories/{id}` returning a story with `story_links`, and
`PUT stories/{id}` with only `workflow_state_id` (Backlog → To Do) have no recorded contract fixture yet (`Fixtures/shortcut`); the
unit tests cover them against the fake board and against the story shape the `list-ready.json` recording holds. Two more reads
first meet the real API there: a story's blockers are each read with their own `GET stories/{id}` and judged finished by the
`type` (`done`) of their workflow state in `GET workflows` (the recording's workflow carries those types), and a released story
the ledger does not know yet is asked for once per run (`GET stories/{id}`: is it back in the Backlog?). The test's runs between
tiers therefore read the released stories; they still add no ledger row and move no story.

What the live test does not cover, and the unit tests do (`PlanReleasePipelineTests`, `PlanRunnerLockTests`): a blocker outside the
plan (held until the factory merges it or the board shows it finished), an archived story, a released story moved back to the
Backlog, a plan whose stories were never recorded (a marked story is refused at claim), a plan that cannot be evaluated (one comment
and one `plan-held` row after `Intake:MaxItemFailures` failures), a closeout the board keeps refusing (the item stays `Planned`,
"post it by hand"), and a Stop landing on a waiting plan after its runner took no worker run lock (deferred to the next run).

## P4-AT2 — a plan across two repositories: a changeset in each, merged in the plan's order (sc-25873)

`MultiRepoPlanAcceptanceTests.P4_AT2_a_plan_across_two_repositories_gets_a_changeset_in_each_and_merges_them_in_the_plans_order`:
`factory run` on a `plan`-labelled story that names two repositories, with the real planner seated in both, a throwaway ledger
and its own work root. The test then drives the plan item and every released story until the plan item is in `Watch`. It is more
expensive than P4-AT1 (one implementer and one review per story, plus one changeset review per repository): keep the work small.

Prerequisites: everything P4-AT1 needs, and
- `FACTORY_E2E_P4_SECOND_REPO=owner/name`: a second sandbox repository with **both GitHub Apps installed** (the workers' App and
  the gate App), protected with `factory github-repo protect owner/name`, a `factory/gate.yaml` on its default branch, CI that
  runs for pull requests whose base is `factory/changeset-*`, and **indexed in CodeGraph** (the gate's `risk-threshold` and the
  planner's tools read it). The test adds it to the factory's configured repositories for its own runs (`GitHub:Watch:Repos`);
  for a real `factory work` it must be in `Factory:DefaultRepo`/`GitHub:Watch:Repos` too.
- `FACTORY_E2E_P4_MULTI_PLAN_STORY=sc-<id>`: a **To Do** story carrying the `plan` label whose description has two lines,
  `Repo: <the sandbox>` then `Repo: <the second repository>`, and describes work that needs a small change in each (e.g. a
  documented format in the first and a reader of it in the second), each story able to pass its repository's gate on its own and
  each repository's changeset inside that repository's `risk` limits.
Stop any `factory work` whose watch scope covers the story's team or epic first, as for P4-AT1.

```sh
FACTORY_E2E=1 FACTORY_E2E_P4_MULTI_PLAN_STORY=sc-<id> FACTORY_E2E_P4_SECOND_REPO=owner/name \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*MultiRepoPlanAcceptanceTests"
```

Expected evidence: the first run ends `Planned`; the `plan` checkpoint has a `repo` on every story, a `merge_order` of both
repositories and a non-empty `interactions` (a planner that put every story in one repository makes the test INCONCLUSIVE);
one `changeset` + `changeset-pushed` pair per repository, all before `plan-stories`, each naming `factory/changeset-sc-<id>`;
every created story's first `Repo:` line is the repository its plan entry names. Each released story's own run ends in `Watch`
with exactly one merged pull request in its own repository whose base is the changeset branch. No story with a
`blocked_by_changesets` entry is released before that repository's `changeset-delivered` row; the second repository's changeset
has no pull request while the first is not delivered. The plan item's transitions hold two `Merge` rows with a `Planned` row
("the changeset of <first> merged as …") between them and end in `Watch`; `changeset-delivered` rows name the repositories in
merge order, each `merged` with its pull request; each repository has exactly one merged changeset pull request whose description
says `The plan changes 2 repositories.`; the two `merge-files` rows name their repositories; one `closeout` `posted` whose comment
lists both repositories as `merged into`.

Not run live yet (2026-10-12): written with sc-25873, to be run by the owner once a second sandbox repository exists. Not
exercised live, covered by `MultiRepoPipelineTests` against two simulated repositories: an upstream changeset pull request that
escalates (the downstream one is not opened; `plan-delivery` and the comment say what is delivered), a downstream one that
escalates after the upstream merged (partial delivery, then fixed and re-run), Stop with one changeset merged and one open, a run
that died after the first repository's branch or between the two changesets, an empty changeset skipped in the order, a
hand-moved story refused while it waits on a changeset; and by `MultiRepoPlanTests`: every validation refusal.

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

Not run live yet (2026-10-12): written with sc-25874, to be run by the owner.

## P4-AT5 — a failed story holds its dependents, and Stop on a plan stops its stories (sc-25876)

`PlanFailureAcceptanceTests.P4_AT5_a_failed_story_holds_its_dependent_the_plan_item_says_so_once_and_stop_on_the_plan_stops_its_stories`:
a fixed plan — A, B that needs A, C and D that need nothing — whose stories the test creates on the real board through the work
source (no planner, no model, no worker), with a throwaway ledger seeded at `Planned`. The stories' own progress is seeded in that
ledger, which is all the plan reads: A Escalated ("seeded by P4-AT5: CI is red"), C in flight (Review), D merged.

Prerequisites: what `factory run` resolves before it starts (the router key, both GitHub Apps' ids and keys, the Shortcut token),
though no router call is made; the workers' GitHub App installed on the default (sandbox) repo with pull-request read (the stop
asks GitHub for open pull requests of `factory/sc-<story>`, and finds none); and `FACTORY_E2E_P4_HOLD_STORY=sc-<id>`: any story on
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
Backlog; the plan item is `Cancelled`, its story in Backlog with one more comment listing `Stories stopped with it: sc-<A>,
sc-<C>. Already merged (left as they are): sc-<D>. Never started (…): sc-<B>.`; its control row is gone; `InFlightAsync` lists
nothing; a second stop writes nothing.

Not run live yet (2026-10-12): written with sc-25876, to be run by the owner. The same class holds one test that is not live and
always runs (the fixed plan passes every plan rule).

What the live test does not cover, and the unit tests do (`PlanClassificationTests`, `PlanFailurePipelineTests`,
`PlanControlsPostgresTests`, `DashboardComponentTests.A_plan_row_shows_…`): a pull request actually turned back into a draft by
a plan's stop (the seeded stories have none; the unit test's story has one), a story whose run is going when the Stop lands
(stopped by that run; the plan's stop unfinished, exit 1, finished by the next poll), a stop that fails half way, a story whose
worker a crashed run left, a workerless run deferring the stop, a Stop that lands while the plan is evaluated (finished by that
run, or deferred by a workerless one) and `factory stop` on a plan whose own run is active (unfinished, exit 1), a released story
nobody claimed (moved back to the Backlog with a comment that does not say the factory worked on it), one a person took, finished,
assigned or archived since its release (nothing written, no ledger item), one moved back to the Backlog (nothing written), a
story merged outside the factory (left as it is and listed apart), a plan Pause with the usage pause also in force (`user-paused`),
a plan Pause in the merge queue (`MergeQueueTests.An_idle_story_whose_plan_is_paused_…`), the control read's query count and its
failing closed, the plan row's `hold comment not posted` note, Pause and Continue on a plan (an in-flight story paused at its
next tool boundary and resumed in the same session; nothing claimed or released meanwhile), a transitive hold, a stuck plan, a
story merged outside the factory, a cancelled story, a failed story that is re-run and merges (`plan-resumed`, its dependents
released), a comment the board refuses (retried, bounded, never posted twice), a story of a stopped plan moved to ready by hand
(claim refused), and the rendered pipeline page.

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
