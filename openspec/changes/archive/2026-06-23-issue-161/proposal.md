## Why

The issue CLI cannot be trusted for real work: `mo issue update <num> --body-file ...` silently wipes the issue's labels because the server PATCH handler treats omitted collection/nullable fields (Labels, IsDraft, AttachmentIds) as "clear" rather than "leave unchanged". At the same time the CLI is missing write verbs that users are forced to reach Web UI or curl for — prerequisites, comments, feedback, reject, and terminal stop — despite all the server endpoints already existing. This change makes PATCH merge semantics correct for all clients (CLI, Web, curl, agents) and completes the issue lifecycle CLI surface.

## What Changes

### A. PATCH merge semantics (server + CLI)

- Server `IssueRoutes.Crud.cs` PATCH handler becomes raw-presence-aware for `Labels`, `IsDraft`, and `AttachmentIds`, matching the existing pattern used for scalar fields (Title/Body/Priority): absent in the raw body preserves the stored value; present-and-null clears; present-with-value replaces.
- CLI `mo issue update` sends `labels` in the PATCH body only when the user explicitly passes `--label/-l`; omitting the flag omits the field entirely.
- Same omit-means-unchanged guarantee extended to every optional update flag (title, body, labels, priority, isDraft).

### B. Execution config flags for create / update

- `mo issue create` gains `--repository <name>`, `--stage-models <json|@file>`, `--stage-model-variants <json|@file>`.
- `mo issue update` gains `--stage-models <json|@file>`, `--stage-model-variants <json|@file>` (repository is immutable post-create).
- `--stage-models` / `--stage-model-variants` support `@file` syntax to read JSON from a file, consistent with `--body @file`.

### C. Write subcommands + missing actions (pure CLI wiring)

- `mo issue prereq add <num> <prereq-num>` → `POST /{n}/prerequisites`
- `mo issue prereq remove <num> <prereq-num>` → `DELETE /{n}/prerequisites/{prereqNo}`
- `mo issue comment add <num> --body ... [--body-file]` → `POST /{n}/comments`
- `mo issue feedback create <num> --stage <s> --body ... [--body-file]` → `POST /{n}/feedback`
- `mo issue reject <num> --message <msg>` → `POST /{n}/reject` (same backend as feedback create, more direct intent)
- `mo issue stop <num>` → `POST /{n}/stop` (terminal stop; `--help` distinguishes from `force-stop` pause semantics)
- All new subcommands support `--project/--project-id` and `-o table|json`.

## Capabilities

### New Capabilities

_None._ All underlying server endpoints and domain behaviors already exist; this change fixes the PATCH contract and wires existing endpoints to the CLI.

### Modified Capabilities

- `http-api`: PATCH `/api/issues/:number` merge semantics change from unconditional full-replacement for Labels/IsDraft/AttachmentIds to raw-presence-aware merge (absent = keep, null = clear, value = replace). The existing "full replacement semantics on update" label requirement is refined.
- `cli-interface`: `create`/`update` gain execution-config flags (`--repository`, `--stage-models`, `--stage-model-variants`); `update` sends `labels` only when `--label` is passed; new write subcommands `prereq add/remove`, `comment add`, `reject`, `stop` are added to the issue command group.
- `approval-feedback-cli`: gains the `feedback create` write command alongside the existing `list`/`show` read commands.

## Impact

- **Server PATCH handler** (`IssueRoutes.Crud.cs`): the raw-body presence-detection pattern used for Title/Body/Priority is extended to Labels, IsDraft, and AttachmentIds. No new endpoints, no domain-model changes.
- **CLI** (`packages/cli/`): `mo issue create`/`update` gain flags; new subcommand groups (`prereq`, `comment`) and new verbs (`feedback create`, `reject`, `stop`) are wired to existing API endpoints. CLI remains a thin client.
- **API consumers**: Web UI, curl users, and future agents that PATCH issues benefit from correct omit-means-unchanged semantics without any client-side change.
- **Tests**: server unit tests for PATCH raw-presence merge (labels/isDraft/attachmentIds); server regression tests for model-metadata persistence via workflow profile path; CLI integration tests for each new subcommand (success + failure paths).
- **Not affected**: issue domain model/invariants, Web UI, server endpoints (no additions), `Issue.Risk` (label-class, deferred to epic #8 / #149).
