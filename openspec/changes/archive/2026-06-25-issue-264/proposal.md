## Why

Archive today is destructive to history: `Issue.Archive()` clears `_activeWorkflowRunId` (`Issue.Transitions.cs:189`), severing the link between a done issue and the workflow run that produced it. Once archived, the issue can no longer surface its timeline, artifacts, events, feedback, or execution context, and the workflow run reference is silently lost. This conflates two unrelated concepts — an issue's *visibility* (archived) and its *execution history* (a workflow run that once ran). The field is even named `ActiveWorkflowRunId`, so every read/control/reconciliation path treats "a reference exists" as "a workflow is actively running," which is wrong for done/archived issues and will only get worse as more lifecycle states are added.

## What Changes

- Archive becomes a **visibility-only** operation: `Archive()` sets `ArchivedAt` and touches `UpdatedAt`, and **no longer clears** the workflow run reference.
- Unarchive only clears `ArchivedAt`; nothing else needs restoring because archive no longer destroys anything.
- **BREAKING (internal naming):** Rename `ActiveWorkflowRunId` / `_activeWorkflowRunId` to `WorkflowRunId` (already aliased publicly as `WorkflowRunId`, but the backing field, `[JsonIgnore]` property, exception text, and logs still use `active`). The reference is renamed to a neutral "workflow run reference" everywhere; "active/running/controllable" becomes a **derived** judgment from issue status + workflow run state, not the mere presence of the id.
- Control paths (start/stop/retry/rerun, profile lock) and the lazy `GetWorkflowStatusAsync` reconciliation stop equating `workflowRunId != null` with "active workflow"; they check issue status and run state explicitly.
- The background `IssueWorkflowReconciliationService` sweep no longer mis-scans archived/done issues as stuck runs: its candidate query excludes non-`InProgress` issues (archived issues are `Done`, so they must not be pulled as candidates).
- Archived issues remain fully readable via the issue detail API/UI: workflow timeline, artifacts, events, feedback, and execution context stay accessible.

## Capabilities

### New Capabilities
- `issue-workflow-run-reference`: The semantics of an issue's workflow run reference as a persistent execution fact — it survives done/archive/close, is decoupled from "active workflow" control state, and is the basis for archived-issue history access. Covers the rename from `ActiveWorkflowRunId` to `WorkflowRunId`, the derived active-workflow judgment, and the archive/unarchive preservation rules.

### Modified Capabilities
- `http-api`: Ensure archived-issue detail responses still expose the workflow run reference and its timeline/artifacts/events/feedback; the read path must not drop history when `ArchivedAt` is set.
- `web-ui`: The archived issue detail page must continue to render the workflow timeline and execution history from the preserved reference (the standalone "Archived" list取数 bug is explicitly out of scope unless it blocks this acceptance).

## Impact

- **Server / Issue Domain** (`packages/server/src/Mohist.Server/Issue/Domain/`): `Issue.cs` (rename `ActiveWorkflowRunId` → `WorkflowRunId`, drop `[JsonIgnore]` aliasing), `Issue.Transitions.cs` (`Archive`/`Close` stop nulling the reference; `Unarchive` unchanged), `WorkflowProfileLockedException` (rename/wording).
- **Server / Issue Grain & Services**: `IssueGrain.cs` (control/retry/reconcile guards read status+run state, not id presence), `IssueWorkflowReconciliationService.cs` (candidate query excludes archived/done), `IssueQuerier.cs` (read path preserves reference for archived issues).
- **Server / Persistence**: `IssueRow` JSON serialization of the renamed field (backward-compatible read of existing rows; verify deserialization handles the rename).
- **Server / Tests**: new coverage for archive-preserves-reference and non-active-workflow-judgment regression; update existing specs that assumed `ActiveWorkflowRunId` naming.
- **No external API contract break** for clients — `WorkflowRunId` is already the public JSON shape; the change consolidates internal naming behind it. Archive/Unarchive HTTP behavior is additive (preserves history) rather than removing data.
