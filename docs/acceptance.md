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
| Worker env is exactly the allowlist for the configured `Worker:Auth` (`router-key`: `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`, `ANTHROPIC_CUSTOM_HEADERS`, `DOTNET_CLI_USE_MSBUILD_SERVER`, `HOME`, `MSBUILDDISABLENODEREUSE`, `PATH`; `claude-login`: the same without `ANTHROPIC_AUTH_TOKEN`) | `LiveWorkerSandboxTests.Live_worker_environment_is_exactly_the_allowlist_for_the_configured_auth_mode` | `FACTORY_SANDBOX_LIVE=1` |
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

No reviewer model needs setting: `Review:Models` defaults to `claude-opus-5` (owner decision 2026-10-09, sc-25391). The
router's catalog has no Opus 5.5 id; its `model_mapping` maps `claude-opus-5` → `claude-opus-5-5`, so the panel pins
`claude-opus-5` under `x-weave-force-model` and accepts the router serving it as `claude-opus-5-5` (a same-or-newer Opus
counts for an Opus pin; a lower Opus, a non-Opus or an unnamed served model does not). An override must still be a Claude
Opus 5 or newer, else these tests, the seeded review (`ReviewSeedTests`) and the Phase 1 AT1–AT5 runs skip naming it and
`factory run`/`factory work` print the reason and exit 2.

| Test | Variables | Proves |
| --- | --- | --- |
| `Gate_merges_a_green_pr_a_claude_opus_panel_passed_and_the_ledger_records_the_merge_commit` | `FACTORY_E2E_GATE_STORY=sc-<id>` | Intake → … → Review → CI → MergeGate → Merge → Watch; one pass verdict from the review panel (sc-25379: correctness and spec conformance, plus security when a touched path's tier requires it), every reviewer a Claude Opus 5 or newer and every second model a Claude model pinned to another id than its reviewer's, each served by the router as pinned (an Opus pin: or a newer Opus), each panel call accounted by the router under its own session, which the ledger named with the role's prompt file hash; the PR merged at the reviewed head; the Merge row holds GitHub's merge commit; the story is Done |
| `A_push_after_the_verdict_blocks_the_merge_until_the_new_head_is_reviewed_again` | `FACTORY_E2E_GATE_PUSH_STORY=sc-<id>` | right after the first review the test pushes one commit to the PR branch (as the workers' App) that keeps the reviewed tree (`SandboxRepo.PushSameTreeAsync`: the head moves, the change stays exactly the story's, so the panel has nothing new to block — a probe file once got blocked as out of scope); checked as invariants (`PushAfterVerdict.Problems`, in-process `PushAfterVerdictTests`), not one transition list, since a fix round after either review is legitimate: a verdict on the pre-push head; after it the item went back to Review; no `gate-passed` on the pre-push head; every `gate-passed` head had a passing verdict before it; transitions end MergeGate → Merge → Watch; the merged PR's head is the last `gate-passed` head (which equals the pushed commit unless a fix round ran) |

```sh
FACTORY_E2E=1 FACTORY_E2E_GATE_STORY=sc-<id> FACTORY_E2E_GATE_PUSH_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*ReviewGateTests"
```

The seeded review (sc-25379) needs only the router and the router key: no GitHub, Shortcut, ledger or gate App. It sends
the fixture diffs `tests/DarkFactory.Orchestrator.Tests/Fixtures/review/*.diff` through the production `RouterReviewer`
to the configured models (`Review:SpecConformance:Models`, else `Review:Models` — a Claude Opus 5 or newer, default `claude-opus-5` — and `Review:Confirm:Models`).
Real models answer, so a failure names what they reported.

| Test | Variables | Proves |
| --- | --- | --- |
| `ReviewSeedTests.The_spec_conformance_prompt_gets_a_real_model_to_flag_an_unused_config_flag_and_a_second_model_confirms_it` | `FACTORY_E2E_REVIEW_SEED=1` | the spec-conformance prompt (`factory/prompts/spec-conformance.md`) gets a real model to report a blocking finding naming `IgnoreBlankInput` on `unused-config-flag.diff` (a flag nothing reads), and the confirm prompt gets the second model to answer `confirmed` |
| `ReviewSeedTests.The_same_flag_with_a_consumer_gets_no_blocking_spec_conformance_finding` | `FACTORY_E2E_REVIEW_SEED=1` | the same flag with a consumer (`consumed-config-flag.diff`: the flag on by default, read by `Count`, with tests of both settings), sent with a story that asks for that setting (`ReviewSeedTests.ConsumedStory`; the unused-flag test keeps its own story), gets no blocking spec-conformance finding. The fixture's consistency is checked in every `dotnet test`: `ReviewFixtureTests` applies it to the WordCount base with `git apply`, `ReviewSeedStoryTests` matches it to the story |

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
main, `sudo scripts/setup-worker-user.sh`).

```sh
FACTORY_E2E=1 FACTORY_E2E_GATE_SEALED_STORY=sc-<id> FACTORY_E2E_GATE_POLICY_STORY=sc-<id> GH_TOKEN=$(gh auth token) \
  FACTORY_E2E_GATE_PASSING_TEST_STORY=sc-<id> FACTORY_E2E_GATE_ROUNDS_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*GateNegativeTests"
FACTORY_E2E=1 FACTORY_E2E_FREEZE_STORY=sc-<id> FACTORY_E2E_STUCK_STORY=sc-<id> FACTORY_E2E_SILENT_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*FreezeAndStuckTests"
```

### AT2 — a To Do sandbox bug story goes end to end with no human touch

Automated: `ReviewGateTests.Gate_merges_a_green_pr_a_claude_opus_panel_passed_and_the_ledger_records_the_merge_commit` (and the
same checks at the end of `A_push_after_the_verdict_blocks_the_merge_until_the_new_head_is_reviewed_again`), variables as in P2-AT1
above. On top of P2-AT1's assertions (states, the Claude Opus panel through the router, the merge commit, the story Done), the run's
ledger is checked row by row (`ReviewGateTests.AssertTypedOutcomesQueueAndReportsAsync`):

- **a typed outcome per step:** every row's `Outcome` equals `StepOutcomes.Of(<the item's state before it>, State, Step, Detail)`;
- **the merge queue:** a `queued` row, then `queue-turn`, then `gate-passed`, then the Merge transition;
- **the PR body matches the ledger:** the merged PR's description equals `LedgerReport.PullRequestBody` of the rows written before
  its rewrite (`pr-report` `merge`) and the worker sessions' recorded costs;
- **the closeout matches the ledger:** a comment on the story equals `LedgerReport.MergedCloseout` of the rows written before the
  `closeout` `posted` row (attributed `[author: dark-factory]`).

The costs are read when the test checks: if the session recorder wrote a cost after the merge, the body check fails showing both
texts (rerun the comparison by hand from the throwaway ledger before it is dropped, or rerun the test).

### AT4 — gate negatives on seeded PRs

Automated: `GateNegativeTests` (`tests/DarkFactory.AcceptanceTests`). The review panel is replaced by a seeded stand-in
(`SeededReviewer`: every role answered as the pinned `Review:Models` model with no finding, or one confirmed blocking finding), so
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

## Upgrading a Phase 1 ledger: items parked at Review

Phase 1 parked every finished item at Review; since sc-25378 Review is a state the factory drives, so the first
`factory work` (or `factory run sc-<id>`) after the upgrade picks up every such item. One whose PR is still open is
reviewed by the panel like any other (Phase 1 recorded no `implementer-model`, which no longer matters: since sc-25379 the
reviewers are Claude Opus whichever models implemented it). One whose PR the owner already handled escalates once, with a
comment on its story:

- "… is merged outside the factory." / "… is closed outside the factory.": the owner already merged or closed the PR.
  Nothing more is needed from the factory: move the story on the board by hand. The item stays Escalated (in-flight
  pickup never resumes an Escalated item).

Expected and one-time: these escalations are the ledger catching up, not failures of the gate.

An item already past Review (at CI or MergeGate) whose head has a passing verdict recorded before sc-25379 — by the
dropped cross-family rule's reviewer (e.g. `gpt-5.5`) or an Opus older than 5 — is not escalated for it: the gate sends
it back to Review and the current panel (`Review:Models`) reviews that head once, then it merges as usual. This happens
once per head: if the new verdict's models still break the rule (e.g. the router serves the configured Opus under another
name), the gate escalates with the reason rather than reviewing again.
