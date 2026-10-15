# Prepare recipes (`.factory/prepare.yaml`, sc-26232)

A repository declares how a checkout is prepared before it is built; the factory runs it. Nothing about the repository is
reshaped for the harness. CLAUDE.md, "Prepare recipes", has the rules; this page is the schema and a worked example.

## Schema (version 1, strict: an unknown or missing key refuses the whole recipe)

```yaml
version: 1
artifacts:                      # optional, at most 16
  - name: <a-z0-9._->           # referenced as ${artifact:<name>}
    url: https://…              # https only; no user info, no fragment; a public host (checked again at connect)
    sha256: <64 hex>            # required; the download is kept only when it matches
    max_bytes: <1 … 4 GiB>      # the download is refused past it (declared length or while streaming)
    unpack: none|zip|tar|tar.gz # optional, default none (the file as downloaded)
    max_unpacked_bytes: <n>     # optional (archives only); default 4 × max_bytes, at most 16 GiB
steps:                          # 1 to 32, run in order; the first failure ends the preparation
  - name: <a-z0-9._->
    argv: [<program>, <arg>, …] # no shell string: an absolute path, a plain name on the helper's fixed PATH,
                                #   or ${artifact:…}/… / ${worktree}/…; never curl, wget, ssh, … (declare an artifact)
    cwd: <relative dir>         # optional, default the repository root; no '..'
    env: {NAME: value}          # optional; PATH, HOME, TMPDIR, DYLD_*, LD_*, ANTHROPIC_* … are refused
    timeout_minutes: <1 … 120>  # optional; always within the run's own Gate:TestTimeoutMinutes
```

Placeholders, in arguments and variable values: `${worktree}` (the checkout being prepared, absolute) and
`${artifact:<name>}` (the artifact's path in the read-only cache: the file itself for `unpack: none`, else the directory it
was unpacked into).

## Example: LensWorks

LensWorks' macOS Xcode build links `.build/rust-catalog/release/liblensworks_catalog.a`, which `cargo` builds from
`adapters/catalog` against Adobe's DNG SDK. The SDK and its pinned SHA-256 are the ones
`engines/catalog/scripts/fetch_dng_sdk.py` names; the archive unpacks to `dng_sdk_1_7_1/`. Homebrew's cargo is on the launch
helper's fixed PATH (there is no rustup there), and the crate's `build.rs` runs `/usr/bin/python3`. A cold build of the
library takes about 2.5 minutes, so the step gets its own bound.

```yaml
version: 1
artifacts:
  - name: dng-sdk
    url: https://download.adobe.com/pub/adobe/dng/dng_sdk_1_7_1_2724_20260908.zip
    sha256: 740fbe95c69e09e9cd17654a5e4fef2d7021254b06fd2b8c5557b79a1496b50c
    max_bytes: 209715200
    unpack: zip
steps:
  - name: rust-catalog
    argv: [/opt/homebrew/bin/cargo, build, --manifest-path, adapters/catalog/Cargo.toml, --locked, --release]
    env:
      CARGO_TARGET_DIR: ${worktree}/.build/rust-catalog
      PHOTOCATALOG_DNG_SDK: ${artifact:dng-sdk}/dng_sdk_1_7_1
      MACOSX_DEPLOYMENT_TARGET: "26.2"
    timeout_minutes: 20
```

This file is not committed to LensWorks by the factory's own work: a change adding it is a LensWorks pull request, reviewed
with the security review (the path is on the gate's floor) and merged by the factory like any build recipe.

## What runs it

- The gate: before every new-tests strategy's build (xUnit, cargo, SwiftPM, Xcode macOS and iOS), each commit's own recipe —
  the base runs use the base's, the head run the head's. Artifacts are fetched owner-side first, outside the worker slot's
  lock; the steps run as the sandbox user. Any failure is the check's `error`, never a pass.
- Workers (`Worker:Prepare=true`): the harness fetches the worktree's recipe artifacts before each implementing session, and
  the prompt names `/usr/local/libexec/dark-factory/factory-prepare --spool <dir>`, which asks the harness (through the
  session's request spool) to fetch the current recipe's artifacts and then runs its steps as the worker.
