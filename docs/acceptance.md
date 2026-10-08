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
   ruleset lists the gate App as a `pull_request`-mode bypass actor (it may merge PRs, never push).
3. A committed `factory/gate.yaml` on the sandbox's main in the version-2 format (sc-25381: path tiers and a risk
   threshold; michaeltrefry/dark-factory-sandbox#6). A version-1 policy is rejected: every review escalates. The gate
   stories must change only normal or free paths (e.g. `src/Sandbox/`, `tests/`), small enough for the risk threshold —
   a sealed or protected path escalates instead of merging. Since sc-25382 a normal-tier change must also add an xUnit test
   that fails on the base and passes on the head (`new-tests-fail-on-base`, run sandboxed as `_factory`, so `sudo
   scripts/setup-worker-user.sh` must be in place), and the policy must list that check in `normal` and `protected`.
4. `Review:Models` set to a Claude Opus 5.5 or newer id the router routes under `x-weave-force-model` (sc-25379: every
   reviewer is one, whichever models the implementer used; there is no default, and the factory refuses to start without
   it — the router's catalog on 2026-10-08 offered no such id). Without it these tests, the seeded review (`ReviewSeedTests`) and the
   Phase 1 AT1–AT5 runs skip naming it; `factory run`/`factory work` print the reason and exit 2.

| Test | Variables | Proves |
| --- | --- | --- |
| `Gate_merges_a_green_pr_a_claude_opus_panel_passed_and_the_ledger_records_the_merge_commit` | `FACTORY_E2E_GATE_STORY=sc-<id>` | Intake → … → Review → CI → MergeGate → Merge → Watch; one pass verdict from the review panel (sc-25379: correctness and spec conformance, plus security when a touched path's tier requires it), every reviewer a Claude Opus 5.5 or newer and every second model a Claude model other than its reviewer's, each served by the router as pinned, each panel call accounted by the router under its own session, which the ledger named with the role's prompt file hash; the PR merged at the reviewed head; the Merge row holds GitHub's merge commit; the story is Done |
| `A_push_after_the_verdict_blocks_the_merge_until_the_new_head_is_reviewed_again` | `FACTORY_E2E_GATE_PUSH_STORY=sc-<id>` | a commit pushed to the PR branch right after the first verdict sends the item from CI back to Review; the new head is reviewed; `gate-passed` and the merge name only the new head |

```sh
FACTORY_E2E=1 FACTORY_E2E_GATE_STORY=sc-<id> FACTORY_E2E_GATE_PUSH_STORY=sc-<id> \
  dotnet test --project tests/DarkFactory.AcceptanceTests -- --filter-class "*ReviewGateTests"
```

The seeded review (sc-25379) needs only the router and the router key: no GitHub, Shortcut, ledger or gate App. It sends
the fixture diffs `tests/DarkFactory.Orchestrator.Tests/Fixtures/review/*.diff` through the production `RouterReviewer`
to the configured models (`Review:SpecConformance:Models`, else `Review:Models` — a Claude Opus 5.5 or newer, which has no default and must be set — and `Review:Confirm:Models`).
Real models answer, so a failure names what they reported.

| Test | Variables | Proves |
| --- | --- | --- |
| `ReviewSeedTests.The_spec_conformance_prompt_gets_a_real_model_to_flag_an_unused_config_flag_and_a_second_model_confirms_it` | `FACTORY_E2E_REVIEW_SEED=1` | the spec-conformance prompt (`factory/prompts/spec-conformance.md`) gets a real model to report a blocking finding naming `IgnoreBlankInput` on `unused-config-flag.diff` (a flag nothing reads), and the confirm prompt gets the second model to answer `confirmed` |
| `ReviewSeedTests.The_same_flag_with_a_consumer_gets_no_blocking_spec_conformance_finding` | `FACTORY_E2E_REVIEW_SEED=1` | the same flag with a consumer (`consumed-config-flag.diff`) gets no blocking spec-conformance finding |

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
worker through the router) against a throwaway ledger, and close every issue they opened.

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
dropped cross-family rule's reviewer (e.g. `gpt-5.5`) or an Opus older than 5.5 — is not escalated for it: the gate sends
it back to Review and the current panel (`Review:Models`) reviews that head once, then it merges as usual. This happens
once per head: if the new verdict's models still break the rule (e.g. the router serves the configured Opus under another
name), the gate escalates with the reason rather than reviewing again.
