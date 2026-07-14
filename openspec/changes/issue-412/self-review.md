# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: T-005 notes stated "Label key constants live in .../AgentSessionQueryMetadataKeys.cs (mohist.io/*)" as if all label keys reside in that one file. The `agentid` key (`mohist.io/agent-id`) actually lives in `GenericAgentSessionMetadata.cs:36`, not in `AgentSessionQueryMetadataKeys.cs`. Verified against both source files. An implementer following the note verbatim would look in the wrong file for the agent-id constant.
  Verification: Updated T-005 notes to reference both files with the specific keys each holds (`AgentSessionQueryMetadataKeys.cs` for project-id/issue-number/source-id/stage/source-kind; `GenericAgentSessionMetadata.cs` for agent-id). Confirmed the edit applied to `tasks.json`.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: completeness
  Evidence: T-004 AC6 ("Issues already linked to an epic at cutover have their Issue.EpicId backfilled from EpicIssueRow in a one-time pass") has no explicit spec-test requirement — the only test gate is "Server build + `npm test` pass", which does not specifically exercise the backfill path. A one-time state backfill that silently no-ops or misses rows would not be caught.
  SuggestedAction: Add a spec assertion (or a dedicated spec scenario) that seeds pre-existing `EpicIssueRow` entries, invokes the backfill, and asserts `Issue.EpicId` is populated afterward.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: `EventCatalog` lists `CheckStarted = "com.mohist.workflow.check.started"` (line 64) but the `WorkflowEvent` union (`WorkflowEvent.cs:5-24`) contains only `CheckPassed`, `CheckFailed`, `CheckPending` — no `CheckStarted` variant. So `workflow.check.started` is catalog-only (no producer). T-001 AC2 names only `runner.disconnected` and `workflow.repair-scheduled` as catalog-only types; `workflow.check.started` is omitted from that list.
  SuggestedAction: Add `workflow.check.started` to the catalog-only examples in T-001 AC2, or confirm it is intentionally excluded.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: alignment
  Evidence: Agent-launch sessions carry an issue number under `GenericAgentSessionMetadata.IssueNumber` = `mohist.io/agent-launch/issue-number`, which differs from the workflow-origin key `AgentSessionQueryMetadataKeys.IssueNumber` = `mohist.io/issue-number` that T-005/D6 project onto the `issue` extension. The spec and matrix scope `issue` to "workflow/issue-origin" sessions, so this is currently consistent — but an agent-launch session launched from an issue context will not get `issue` stamped on its events.
  SuggestedAction: Confirm whether agent-launch sessions associated with an issue should also stamp `issue` (reading from `mohist.io/agent-launch/issue-number`), or document that only workflow-origin sessions carry issue lineage.
  Status: follow-up

<promise>PASS</promise>
