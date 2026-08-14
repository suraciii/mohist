## Why

A managed CLI update can publish a candidate and commit the managed runtime
pointer while the stable `mo` path still executes an older direct binary. A
successful update must describe the command users actually invoke, not only a
release directory that is no longer on their command path.

## What Changes

- Treat the stable launcher path as part of the managed CLI activation
  transaction.
- Migrate a previous direct executable at the stable or explicit CLI path to a
  launcher that atomically delegates to the candidate executable. An explicit
  `--cli-path` is the entrypoint that is activated and verified; it is not an
  advisory value.
- Require an explicit `--cli-path` to be an existing absolute entrypoint before
  candidate staging. Missing or relative paths fail closed without changing the
  runtime.
- Verify the candidate source revision by invoking the exact activated path
  before the runtime pointer is committed.
- Restore the preceding launcher and managed runtime pointer when activation,
  verification, or commit fails.
- Provide a reachable first-deployment bootstrap: `scripts/install-mo.sh`
  publishes the current CLI directly to the stable user path. The old CLI must
  not be expected to run an unreachable legacy `mo update cli` path in order to
  install this behavior.

## Scope

This change is limited to managed CLI update activation, first-deployment
bootstrap guidance, and focused CLI tests. It does not change Server, Runner,
Slack, workflow, or runtime-specific behavior.
