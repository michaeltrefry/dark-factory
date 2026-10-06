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
the same worktree, and never pushes or opens a PR twice. A worker the killed run left
running is stopped (its whole process group) before the resume. If the worktree is gone after
the branch was pushed, it is re-created from the pushed branch rather than redone. Ctrl-C
pauses the item (a re-run unpauses it); any other failure, including a timed-out API call,
escalates it and comments on the story; re-running an escalated item first posts that comment
if it failed, then starts it again from Intake. Only one `factory run` drives an item at a
time (a Postgres advisory lock); a second exits non-zero with "already running".

Workers run as a dedicated hidden macOS user (`_factory`), not as you — see
[Worker sandbox](#worker-sandbox).

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

# 2./3. Secrets in the login keychain: copy each one to the clipboard, then run its line.
#    `security add-generic-password -w` with no value (interactive prompt) silently cuts input at
#    128 characters — Shortcut tokens (sct_rw_<workspace>_…) are longer and then fail with 401 —
#    and `-w '<value>'` puts the secret in argv (visible to ps). `security -i` reads the command on
#    stdin and printf is a shell builtin, so the value never reaches argv and is not truncated.
# 2. Shortcut API token for the orchestrator (or export SHORTCUT_API_TOKEN)
printf 'add-generic-password -U -s dark-factory -a shortcut-api-token -w %s\n' "$(pbpaste)" | security -i
# 3. Router key (or export FACTORY_ROUTER_KEY)
printf 'add-generic-password -U -s dark-factory -a router-key -w %s\n' "$(pbpaste)" | security -i
#    Verify (with the same value still on the clipboard; prints only the result):
[ "$(security find-generic-password -s dark-factory -a router-key -w)" = "$(pbpaste)" ] && echo stored-ok || echo MISMATCH
#    (use -a shortcut-api-token to check the Shortcut token the same way)

# 4. Worker sandbox user, launch helper, sudoers rule and work root (see below)
sudo scripts/setup-worker-user.sh

# 5. Worker model auth — pick one:
#    a) router passes through the worker's own Claude login (default, Worker__Auth=claude-login).
#       Log the _factory user in once; never copy your own credentials:
sudo -u _factory -H /Users/_factory/.local/bin/claude   # then /login, then /exit
#    b) router holds BYOK provider keys: export Worker__Auth=router-key (nothing else needed)
```

### Worker sandbox

`scripts/setup-worker-user.sh` (run once with sudo; safe to re-run) sets up:

- **`_factory`**: a hidden role account (UID/GID in 400–499, own group, shell `/usr/bin/false`,
  home `/Users/_factory`, mode 0700) with its own Claude Code install in `~/.local/bin`.
  dotnet (`/usr/local/share/dotnet`) and git (`/usr/bin/git`) are used system-wide; builds run natively.
- **An ACL deny** for `_factory` on your home directory, so it can't list or traverse
  `~`, `~/.ssh`, `~/.config/gh` or `~/Library/Keychains`.
- **`/usr/local/libexec/dark-factory/factory-worker-launch`** (root-owned) and
  **`/etc/sudoers.d/dark-factory`**: you may run *only* that helper, *only* as `_factory`, without a password.
  The orchestrator sends the router variables on the helper's stdin; the helper starts the worker with
  exactly `PATH`, `HOME` (`_factory`'s), `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`
  (no lingering build servers) and the router URL/headers (plus the router key as `ANTHROPIC_API_KEY` in
  `router-key` mode). It refuses any other variable and any program that is not an absolute path or a
  plain command name. When the worker exits, or its stdin closes (Stop, timeout, or the orchestrator
  dying), it kills the worker's tree and process group and then **every `_factory` process**
  (`kill -1` as `_factory`), so nothing that forked and `setsid()`ed away survives the run.
  That makes `_factory` single-tenant: **one sandboxed `factory run` at a time per machine**, enforced by
  a lock on `<work root>/.factory-run.lock` (a second run fails fast).
- **`/opt/dark-factory/work`**: the work root (clones + worktrees), owned by you under a root-owned
  parent, readable by `_factory` (an inheritable ACL on the work root itself, never applied recursively).
  Each worktree directory is created empty and shared first (an inheritable read/write ACL for you and
  `_factory`), then checked out into, so the files inherit the entry — a recursive `chmod` would follow
  committed symlinks to their targets. The worktree is deleted once the PR is open or the item escalates
  (deleted first as `_factory`, then you remove anything left); a paused (Ctrl-C) or crashed Implement keeps
  it for the re-run to resume in. Each run starts by stopping leftover `_factory` processes and sweeping
  worktrees no run will resume.

The worker only edits files; the orchestrator (you) commits and pushes with the scoped App token. Owner
git never trusts the worktree's worker-writable `.git` file (it passes `--git-dir`/`--work-tree`), and the
worktree directory itself is created by you, so `safe.directory` never applies. The work root is kept out of
`_factory`'s home on purpose: anything inside a directory the worker owns could be swapped (e.g. for a symlink)
under your git.

Set `Worker__RunAs=none` to run workers as yourself (development only; no isolation).

Live checks (skip until the setup has run):

```sh
dotnet test --project tests/DarkFactory.Orchestrator.Tests --filter-class "*LiveWorkerSandboxTests"
```

### Live acceptance harness

```sh
FACTORY_E2E=1 FACTORY_E2E_STORY=sc-<to-do bug story> dotnet test --project tests/DarkFactory.AcceptanceTests
```

Each test skips with the exact missing prerequisite when a credential, the router,
the ledger database or the Claude CLI is unavailable. See `CLAUDE.md` for all config keys.
