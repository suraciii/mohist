## Why

WorkflowRun control is scattered across three command trees today: `mo workflow approve/retry/stop/...`, `mo issue approve/retry/stop/...`, and the run-scoped reads under `mo workflow get/events/variables`. A user starting work with `mo issue start` cannot hand the resulting Run to a single navigation point, and must guess which of `issue`, `workflow`, or `run` holds the canonical approve, retry, or stop. This consolidates all WorkflowRun viewing and control into `mo run`, makes `mo issue start` return the Run ID, and deletes the duplicate entry points so there is exactly one path per capability.

## What Changes

- Introduce `mo run` as the single command tree for WorkflowRun navigation and control: `list`, `view`, `watch`, `approve`, `reject`, `retry`, `rerun`, `pause`, `resume`, `stop`, and `feedback list/view`.
- Every `mo run` command that targets a specific Run accepts exactly one of: a positional WorkflowRun ID, or `--issue <number>` (with optional `--project`). Providing both or neither is a usage error that fails locally without a remote call.
- `mo issue start <number>` returns the created or already-bound WorkflowRun ID so the user can immediately pass it to `mo run`.
- `run retry` retries the current failure point and restores the manual-retry budget; `run rerun` reruns the whole Run from the start, or from a specific stage via `--from-stage`. The two are not interchangeable.
- `run pause` leaves a `resume` entry; `run stop` is a permanent, non-resumable termination. `stop` requires explicit confirmation in non-interactive contexts via `--yes`.
- **BREAKING**: Remove WorkflowRun control verbs from `mo issue` — `approve`, `reject`, `retry`, `rerun`, `rerun-from-stage`, `force-stop`, `resume`, and `stop` no longer appear in the issue command tree, help, or error hints.
- **BREAKING**: Remove the old `mo workflow` execution entry points (`approve`, `reject`, `retry`, `rerun`, `resume`, `pause`, `stop`, `get`/`show`, `variables`, `events`, `list-sessions`). Their behaviors move to `mo run` or to the command group that owns the associated resource in the target surface.
- Move approval feedback reads to `mo run feedback list/view`; feedback no longer appears under `mo issue` or `mo session`.
- `mo issue` retains work-item CRUD, lifecycle (`start`, `done`, `close`, `reopen`, `archive`, `restore`, `rebase`), relations, comments, prerequisites, templates, diff, and commits.

## Capabilities

- `run-control`: The seven state-changing verbs (`approve`, `reject`, `retry`, `rerun`, `pause`, `resume`, `stop`) live exclusively under `mo run`. Covers target resolution (Run ID or `--issue`, exactly one), distinct state semantics for each verb, `rerun --from-stage` as a flag variant, and `--yes` confirmation for the irreversible `stop`.
- `run-reads`: `list`, `view`, and `watch` under `mo run`. `view`/`watch` use the same target-resolution contract as control verbs; `list` enumerates WorkflowRuns. Absorbs the current `workflow get`/`show` read.
- `run-feedback`: `feedback list` and `feedback view` under `mo run`, reading approval feedback for the targeted Run.
- `issue-start-binding`: `mo issue start` creates or binds a WorkflowRun and returns its ID. Establishes the boundary that `mo issue` owns work-item lifecycle and relationships but never WorkflowRun state transitions. Asserts the absence of removed control paths and old `workflow` execution entries from the command tree, help, and error hints.

## Impact

- **CLI command tree** (`packages/cli/Mohist.Cli/`): new `RunCommands` group registered at root; `WorkflowCommands` execution subcommands removed; `IssueCommands` loses control verbs (`BuildAction("approve"/"retry"/"force-stop"/"resume")`, `BuildReject`, `BuildRerun`, `BuildRerunFromStage`, `BuildStop`) and feedback moves out. `issue start` output surfaces `workflowRunId`.
- **Target resolution**: a shared resolver translating `--issue <number>` (+ `--project`) to a WorkflowRun ID, reused across all run-control, run-reads, and run-feedback commands; fails locally on both-provided or neither-provided.
- **Server API**: no new endpoints required — run commands target existing `/api/workflow-runs/{id}` routes; `--issue` resolution reads the issue's bound `workflowRunId`. `issue start` already returns the issue resource carrying `workflowRunId`.
- **Tests** (`packages/cli/tests/`): `CliWorkflowControlSpecs`, `CliIssueRejectAndStopSpecs`, `CliIssueRerunFromStageSpecs`, `CliIssueCommandSpecs`, `CliIssueCommentAndFeedbackSpecs` and related specs updated to the `run` command surface; new specs for target-resolution mutual exclusion, `--yes` confirmation, and `issue start` Run ID return.
- **Docs** (`docs/cli-reference.md`, `docs/issues.md`): update examples from `mo issue approve/retry/stop` to `mo run ...`; narrow the "实装差距" section for the items this change closes.
