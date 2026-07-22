## Context

The CLI currently exposes WorkflowRun control through two overlapping command trees:

- `mo workflow <verb> <run-id>` — run-scoped control (approve, reject, retry, rerun, resume, pause, stop) and reads (get/show, variables, events, list-sessions). Registered at root via `WorkflowCommands.Build` in `MohistCliCommands.cs:21`.
- `mo issue <verb> <number>` — issue-scoped control (approve, reject, retry, rerun, rerun-from-stage, force-stop, resume, stop) and feedback. Registered in `MohistCliCommands.Issue.cs:17-28`.

Both paths resolve to the same server grain methods — the issue-scoped endpoints (`/api/projects/{projectRef}/issues/{number}/approve`, etc.) internally read the issue's `workflowRunId` and delegate to `IWorkflowGrain`, exactly as the run-scoped endpoints (`/api/workflow-runs/{id}/approve`) do. The result is that a user cannot predict which command tree holds the canonical action, and `mo issue start` does not prominently surface the Run ID needed to navigate the result.

The prerequisite issue #475 established shared CLI contracts: `--project` resolution (single option, no `--project-id`), `--json` field selection, stdout/stderr separation, exit codes (0/1/2/130), and non-interactive behavior (`MOHIST_PROMPT_DISABLED`). This change builds on those contracts.

The server already has all run-scoped endpoints needed: `/api/workflow-runs/{id}/{approve,reject,retry,rerun,rerun-from-stage,resume,pause,stop}` and `GET /api/workflow-runs/{id}` for detail. No server-side changes are required.

## Goals / Non-Goals

**Goals:**

- Establish `mo run` as the single command tree for all WorkflowRun navigation and control.
- Add `--issue <number>` target resolution so run commands can be addressed by issue number, resolving to the issue's bound `workflowRunId`.
- Make `mo issue start` surface the WorkflowRun ID in its output.
- Delete the duplicate control paths from `mo issue` and the entire `mo workflow` execution tree.
- Move feedback reads to `mo run feedback`.
- Add `--yes` confirmation for the irreversible `stop` verb in non-interactive contexts.

**Non-Goals:**

- No WorkflowProfile collection management (the `workflow` command's future as a Profile manager is a separate slice).
- No Project/Issue/Run Variables CLI (separate slice).
- No AgentSession migration (separate slice).
- No renaming of surviving `show`/`update` verbs or other historical aliases (final consolidation batch).
- No removal of server-side issue-scoped endpoints — they remain for the web UI.
- No removal of `issue workflow status/timeline/config` — these are reads and profile configuration, not WorkflowRun control verbs.

## Decisions

### D1: New `RunCommands` class, mirroring the WorkflowCommands pattern

Create a new `static partial class RunCommands` with `Build(MohistCliApi api)` registered at root in `MohistCliCommands.cs`. Split by concern:

- `MohistCliCommands.Run.cs` — `Build()`, shared target resolver, control verbs (approve, reject, retry, rerun, pause, resume, stop).
- `MohistCliCommands.Run.Reads.cs` — list, view (with `--yaml`), watch.
- `MohistCliCommands.Run.Feedback.cs` — feedback list, feedback view.

This mirrors how `WorkflowCommands` is split into `Workflow.cs` + `Workflow.Reads.cs`, and how `IssueCommands` is split across many partial files.

**Alternative considered:** A single monolithic file. Rejected — the issue commands are already split this way and consistency aids navigation.

### D2: Shared target resolver with local mutual-exclusion validation

Every run command that targets a specific Run shares a `ResolveRunTargetAsync` helper:

```
Input:  positional runId (string?), --issue option (string?), --project (string?)
Output: (string runId, int exit)  — exit != 0 means fail locally, no HTTP call
```

Validation order:
1. If both `runId` and `--issue` are provided → exit 2, stderr: "Provide either a Run ID or --issue, not both."
2. If neither is provided → exit 2, stderr: "A Run ID or --issue <number> is required."
3. If `runId` provided → return it directly. No project resolution, no HTTP call.
4. If `--issue` provided → resolve project via `api.ResolveProject(project)`, then GET `/api/projects/{projectId}/issues/{number}`, read `workflowRunId`. If null/empty → exit 1, stderr: "Issue #N has no active workflow run."

Steps 1–2 execute before any HTTP request, satisfying the spec's local-failure requirement. The resolver returns the resolved Run ID; the calling verb then constructs the run-scoped endpoint path and proceeds.

**Alternative considered:** Resolve `--issue` server-side by having the run-scoped endpoints accept an issue number query parameter. Rejected — it would couple run-scoped endpoints to project/issue resolution and complicate the server, when the CLI can do the one-shot lookup itself.

### D3: `run list` derives from the issues list — no new server endpoint

The server has no `GET /api/workflow-runs` collection endpoint. Rather than adding one, `run list` derives its output from the existing issues list:

1. GET `/api/projects/{projectId}/issues`
2. Filter to issues where `workflowRunId` is non-null
3. Project each to `{ id: workflowRunId, status, stage, issueNumber }`

This works because the workflow engine keeps the issue's `status` and `stage` fields synchronized with the run's state. The derivation gives exactly the fields the spec requires (Run ID, status, stage, issue number) without server changes.

**Alternative considered:** Add `GET /api/projects/{projectId}/workflow-runs` server endpoint. Rejected for this change — it would expand scope into server code and the derivation is sufficient. If run-level detail beyond what the issue carries is needed later, a dedicated endpoint can be added in a follow-up.

### D4: `run watch` polls the run detail endpoint

The server's events endpoint (`GET /api/workflow-runs/{id}/events`) returns a bounded JSON array, not an SSE stream. `run watch` therefore uses polling:

1. GET `/api/workflow-runs/{id}` — capture current status/stage.
2. Print a compact JSON status line to stdout (NDJSON).
3. Sleep a fixed interval (e.g. 2 seconds, injectable `TimeProvider` for tests).
4. Repeat until the run reaches a terminal status (completed, stopped, cancelled) or the user interrupts (Ctrl-C → exit 130).
5. On status/stage change between polls, print the new state.

The poll interval and `TimeProvider` are injected so tests use fake timers (per `design/testing.md` — no wall-clock assertions).

**Alternative considered:** Convert the events endpoint to SSE for true streaming. Rejected — it requires server changes and the polling approach is simpler, testable, and sufficient for the CLI's use case.

### D5: `stop --yes` confirmation uses the existing prompt infrastructure

The `CliInvocation.PromptsEnabled` property already distinguishes interactive from non-interactive contexts (checks `ICliTerminal.IsInputInteractive` and `MOHIST_PROMPT_DISABLED`). The `stop` verb adds:

- A `--yes` option that bypasses confirmation unconditionally.
- When `PromptsEnabled` is true and `--yes` is not set: prompt on stderr "Stop <runId> permanently? This cannot be undone. [y/N]", read confirmation from stdin.
- When `PromptsEnabled` is false and `--yes` is not set: exit 1, stderr: "--yes is required to confirm this irreversible action."

No other control verb requires confirmation. `pause` is reversible (via `resume`); `rerun`/`retry` do not permanently destroy the run.

### D6: `issue start` surfaces `workflowRunId` via descriptor and table output

Today `BuildAction("start", ...)` POSTs to `/issues/{number}/start` and renders the result via `PrintMutationResourceAsync` with `IssueDescriptor`. The issue resource already carries `workflowRunId` (confirmed in the `mo issue show` API response). Two adjustments:

1. Add `workflowRunId` to the `IssueDescriptor` fields list in `Issue.CrudReads.cs` so `--json workflowRunId` selects it. (The current descriptor lists `workflowRun` which does not match the API field name `workflowRunId`.)
2. Ensure the `IssueShow` table renderer displays `workflowRunId` prominently. The table renderer in `TableRenderer` already renders issue fields; adding `workflowRunId` to the visible set makes it appear in the default output.

No new server endpoint or response shape is needed — the field is already returned.

### D7: Remove `workflow` command entirely; update root shape tests

After removing all execution subcommands, the `workflow` command would be an empty shell. Profile management is a Non-Goal. Therefore:

- Remove `WorkflowCommands.Build(api)` from root registration in `MohistCliCommands.cs`.
- Delete `MohistCliCommands.Workflow.cs` and `MohistCliCommands.Workflow.Reads.cs`.
- Update `CliRootCommandShapeTests`: remove `"workflow"` from `survivingResourceGroups`, add `"run"`.

The web UI's issue-scoped server endpoints (`/api/projects/{projectRef}/issues/{number}/approve`, etc.) are NOT removed — they serve the web UI's action buttons. Only the CLI command registrations are deleted.

### D8: Retain `issue workflow status/timeline/config` and `issue events/logs`

The acceptance criteria require removing WorkflowRun **control** verbs and feedback from `issue`. The following issue subcommands are reads or profile configuration, not control, and remain:

- `issue workflow status` / `timeline` — WorkflowRun reads via issue scope.
- `issue workflow config get/set/clear` — issue's WorkflowProfile override management.
- `issue events` / `issue logs` / `issue diff` / `issue commits` — issue-scoped reads.
- `issue session` / `issue sessions` — AgentSession reads (Non-Goal: no session migration).

These will be consolidated in the final cleanup batch per the Non-Goals.

### D9: Feedback target resolution uses run-scoped or issue-scoped path

Feedback records are currently stored at the issue scope (`GET /api/projects/{projectId}/issues/{number}/feedback`). The `run feedback` commands resolve the target the same way as control verbs (D2), then need to read feedback. Two resolution paths:

- **Run ID provided:** The run detail response (`GET /api/workflow-runs/{id}`) includes `issueRef` (issue number and project). Use that to construct the issue-scoped feedback path.
- **`--issue` provided:** The issue is already resolved; use it directly for the feedback path.

No run-scoped feedback endpoint exists or is needed — the CLI resolves the associated issue and reads feedback from the existing issue-scoped endpoint.

## Risks / Trade-offs

- [Target misresolution sends control action to wrong Run] → The mutual-exclusion check (D2) fails locally before any HTTP call. The `--issue` resolver reads `workflowRunId` from the issue resource, not from user input, so a stale issue number resolves to whatever run is currently bound. Tests assert exact endpoint paths for every verb × target combination.

- [`run list` derivation from issues misses runs whose issue was deleted] → If an issue is hard-deleted, its run would not appear in `run list`. This is acceptable because hard-deleted issues are not a normal operational state; the run is still directly addressable by ID via `run view <id>`.

- [`run watch` polling adds latency vs. true streaming] → A 2-second poll interval means status changes appear with up to 2s delay. This is acceptable for a CLI watch command. The interval is injectable for tests.

- [Removing `workflow` command breaks scripts using `mo workflow <verb>`] → This is a BREAKING change by design (per the issue: "旧 Issue/WorkflowRun 控制路径和内建 alias 直接删除"). The project is in active development with no version compatibility constraint (AGENTS.md). Users migrate to `mo run <verb>`.

- [`issue workflow status/timeline` remaining creates a temporary inconsistency with the target spec] → The target `docs/cli-reference.md` does not list `issue workflow` as a subcommand. Retaining it temporarily is explicitly noted in the Non-Goals and will be resolved in the final consolidation batch.

## Migration Plan

This is a single atomic CLI change — no phased rollout or backward-compatible period is needed (active development, no version constraint).

**Steps:**

1. Create `RunCommands` files (Run.cs, Run.Reads.cs, Run.Feedback.cs) with all verbs, target resolver, and shared options.
2. Register `RunCommands.Build(api)` at root in `MohistCliCommands.cs`.
3. Remove control verb registrations from `MohistCliCommands.Issue.cs` (approve, retry, rerun, rerun-from-stage, force-stop, resume, reject, stop, feedback).
4. Delete `MohistCliCommands.Issue.Feedback.cs`.
5. Clean up `MohistCliCommands.Issue.Lifecycle.cs` — remove `BuildReject`, `BuildRerun`, `BuildRerunFromStage`, `BuildStop`; retain `BuildAction`, `BuildRebase`, `BuildArchive`, `BuildGetSub`.
6. Delete `MohistCliCommands.Workflow.cs` and `MohistCliCommands.Workflow.Reads.cs`.
7. Remove `WorkflowCommands.Build(api)` from root registration.
8. Add `workflowRunId` to `IssueDescriptor` fields; update `IssueShow` table rendering if needed.
9. Update `ResourceOutput.cs` — add `RunList` TableShape and descriptor; verify `WorkflowRunDetail` descriptor fields are adequate for `run view`.
10. Update/add tests (see Test Impact below).
11. Update `docs/cli-reference.md` and `docs/issues.md` examples.

**Rollback:** Revert the commit. No data migration or server state is involved.

### Test Impact

**Rewrite** (command surface changes from `workflow`/`issue` to `run`):
- `CliWorkflowControlSpecs.cs` → rename to `CliRunControlSpecs.cs`; update all assertions from `["workflow", verb, ...]` to `["run", verb, ...]`; remove issue regression tests (those verbs no longer exist).
- `CliIssueRejectAndStopSpecs.cs` → delete (verbs removed from issue).
- `CliIssueRerunFromStageSpecs.cs` → delete (verb removed from issue).
- `CliIssueCommentAndFeedbackSpecs.cs` → remove feedback tests (moved to run).
- `CliWorkflowReadsSpecs.cs` → rename to `CliRunReadsSpecs.cs`; update command paths.
- `CliRootCommandShapeTests.cs` → remove `"workflow"` from `survivingResourceGroups`, add `"run"`.

**New test files:**
- `CliRunControlSpecs.cs` — target resolution mutual exclusion, `--issue` resolution, `stop --yes` confirmation, server error surfacing, `--json` field selection.
- `CliRunReadsSpecs.cs` — list derivation, view by Run ID / `--issue`, `--yaml` mutual exclusion, watch polling with fake timers.
- `CliRunFeedbackSpecs.cs` — feedback list/view under run, `--issue` targeting, removed issue feedback path.
- `IssueStartRunIdSpecs.cs` — `issue start` output contains `workflowRunId`; `--json workflowRunId` selection.

**All tests** use the existing `RecordingHttpHandler` + `CliTestFactory` pattern; no real network, process, or wall-clock dependencies (per `design/testing.md`).

## Open Questions

- **`run view` output shape:** The current `workflow get` renders via `WorkflowRunDetail` table shape with fields `["status", "issueRef"]`. Should `run view` expand the default visible fields (e.g. `currentStage`, `stages`, `approvalState`)? The spec says "full WorkflowRun resource" — the table renderer should show enough for a human to assess run state without `--json`. Decision deferred to implementation; the descriptor fields can be expanded without breaking the API contract.

- **`run watch` output detail:** Should each poll print the full run detail, or only the changed fields? Printing only changes is cleaner for streaming but requires diffing. For v1, printing a compact status line (`{id, status, stage}`) on each change is sufficient; full detail is available via `run view`.

- **`--dry-run` on run control verbs:** The current `workflow` commands support `--dry-run`. Should `run` commands preserve it? The `docs/cli-reference.md` target says "不提供'一律支持'的 --dry-run". The specs do not require it. Decision: do not add `--dry-run` to run commands. If needed, it can be added per-verb in a follow-up.
