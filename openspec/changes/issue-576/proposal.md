## Why

A managed CLI update can publish a candidate and commit the managed runtime
pointer while the stable `mo` path still executes an older direct binary. A
successful update must describe the command users actually invoke, not only a
release directory that is no longer on their command path.

## What Changes

- Treat the stable launcher path as part of the managed CLI activation
  transaction.
- Migrate a previous direct executable at the stable or explicit CLI path to a
  launcher that atomically delegates to the candidate executable.
- Verify the candidate source revision by invoking that launcher before the
  runtime pointer is committed.
- Restore the preceding launcher and managed runtime pointer when activation,
  verification, or commit fails.

## Scope

This change is limited to managed CLI update activation and its focused CLI
tests. It does not change Server, Runner, Slack, workflow, or runtime-specific
behavior.
