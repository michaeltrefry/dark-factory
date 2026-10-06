# dark-factory
A dark software factory for AI development

## Walking skeleton: `factory run`

`factory run sc-<id>` reads one Shortcut story, records it in the ledger (Postgres),
creates a git worktree of the target repo on `factory/sc-<id>`, runs Claude Code
headless (`claude -p --output-format stream-json`) through the Weave router at
`localhost:8080`, pushes the branch with a GitHub App installation token scoped to
that one repo, and opens a PR whose body links the story. The ledger records
`Intake → Implement → Review` with timestamps and the Claude session id; the router's
`GET /v1/sessions/<session-id>/cost` reports the run's cost.

The target repo is `michaeltrefry/dark-factory-sandbox` unless the story description
has a `Repo: owner/name` line.

### Lifecycle and resume

Items move through an explicit transition table (`Ledger/Lifecycle.cs`): Intake → Plan →
Implement → Review ⇄ Fixing (max 3 rounds) → CI ⇄ CI healing → Merge gate → Merge → Watch →
Done, with Escalated, Paused and Cancelled reachable from any non-terminal state. Phase 1
runs Intake → Implement → Review and parks the item at Review with the PR open.

`factory run sc-<id>` is idempotent: on an existing item it continues from the last ledger
row. A run killed during Implement resumes the same Claude session (`claude --resume`) in
the same worktree, and never pushes or opens a PR twice. Ctrl-C pauses the item (a re-run
unpauses it); a failure escalates it and comments on the story; re-running an escalated item
starts it again from Intake.

**Security caveat:** until worker isolation (story S4) lands, workers run as the owner's
macOS user and can reach the owner's keychain and gh/git credentials, so only run
`factory run` against the sandbox repo with trusted stories.

### Build and test

```sh
docker compose up -d
dotnet tool restore
dotnet build
dotnet test
```

### One-time owner setup

```sh
# 1. Register the GitHub App (opens a browser; stores app id + private key in the login keychain)
dotnet run --project src/DarkFactory.Orchestrator -- github-app setup
#    then install the app on michaeltrefry/dark-factory-sandbox via the printed link

# 1b. Protect the target repo (main needs a PR; the App can only write factory/**). Uses your own
#     GH_TOKEN or `gh auth token` (repo admin). Private repos on a free personal plan need GitHub Pro.
#     Non-admin integrations (Dependabot, GITHUB_TOKEN deploys like gh-pages) can then only write factory/**.
dotnet run --project src/DarkFactory.Orchestrator -- github-repo protect michaeltrefry/dark-factory-sandbox

# 2. Shortcut API token for the orchestrator (or export SHORTCUT_API_TOKEN)
security add-generic-password -U -s dark-factory -a shortcut-api-token -w '<token>'

# 3. Router key (or export FACTORY_ROUTER_KEY)
security add-generic-password -U -s dark-factory -a router-key -w '<rk_...>'

# 4. Worker model auth — pick one:
#    a) router passes through Claude's own login (default, Worker__Auth=claude-login):
claude   # then /login once in a terminal as this macOS user
#    b) router holds BYOK provider keys: export Worker__Auth=router-key
```

### Live acceptance harness

```sh
FACTORY_E2E=1 FACTORY_E2E_STORY=sc-<to-do bug story> dotnet test --project tests/DarkFactory.AcceptanceTests
```

Each test skips with the exact missing prerequisite when a credential, the router,
the ledger database or the Claude CLI is unavailable. See `CLAUDE.md` for all config keys.
