# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `specs/issue-workflow-completion/spec.md` scenario "Issue resolution derives from the workflow run" normatively asserted the handler SHALL "resolve the owning `issueId` from the completed workflow run's **issue context**". This contradicts `design.md` (Context + Decision 2), which grounds that `com.mohist.workflow.run.completed` and the `WorkflowRun` aggregate carry **no issue context** (`WorkflowRunCompleted` is an empty record; `WorkflowRunMetadata` has no issueId/projectId), so resolution MUST be a reverse DB lookup on the indexed `IssueRow.WorkflowRunId` filtered to `InProgress`. T-001's description and acceptance criteria already specify the reverse-lookup mechanism correctly, so the spec was the lone outlier. Changed the scenario's `THEN` clause to state the reverse-lookup keyed on the run id (filtered to `InProgress`), aligning the normative requirement with the design and T-001.
  Verification: Re-read the edited scenario; it now matches design Decision 2 and T-001's "IssueQuerier.GetIssueIdForWorkflowRunAsync filters Issues by WorkflowRunId = @id AND Status = 'inProgress'". No behavior or architectural change — wording only.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: The accepted transition-period gap (best-effort in-memory event delivery; sweep + lazy reconcile removed leaves no automatic fallback) is correctly documented in proposal Impact, design Risks, and the `issue-workflow-run-reference` REMOVED requirement Migration note. It is explicitly a Non-Goal of this issue (durable at-least-once mechanism deferred). No action required within issue-307.
  SuggestedAction: Track the durable event mechanism (transactional outbox + dispatcher + DLQ, or event-store replay) as a follow-up issue; re-confirm the handler's at-least-once redelivery safety when it lands (design Open Questions already flags this).
  Status: follow-up

## Review Summary

- **Alignment**: All three issue workstreams (event-driven completion bridge, removal of daily sweep + lazy read-path reconciliation, removal of synthetic Web "Done" stage) are fully traced in proposal "What Changes", design Decisions, specs, and tasks. Every issue acceptance criterion maps to a spec scenario and a task acceptance item (incl. injectable-time verification, idempotency, failed/stopped exclusion, sweep+test deletion, pure read path, four Web surfaces, and green `dotnet test` / web `typecheck` + `test:run`).
- **Completeness**: Three capabilities (`issue-workflow-completion` new; `issue-workflow-run-reference`, `web-ui` modified) each have a spec file and exactly one owning task (T-001/T-002/T-003). Edge cases covered: duplicate delivery, mismatched workflowRunId, failed/stopped no-op, read-path never mutates, kanban Done-status override removed. The removed "Background reconciliation skips non-in-progress issues" requirement is documented with Reason + Migration.
- **Consistency**: Spec anchors in tasks resolve exactly to the Requirement headings. Naming (`IssueWorkflowCompletionHandler`, `GetIssueIdForWorkflowRunAsync`, `ReconcileWithWorkflowTerminalStateAsync`, `IssueWorkflowReconciliationService`) is uniform across proposal/design/specs/tasks. The one spec/design wording contradiction (item-1) is repaired.
- **Feasibility**: Task titles name feature slices, not technical micro-actions; no over-granular tasks; tests are embedded in each implementation task (no standalone "add tests" task). T-001 → T-002 ordering is sound (new primary path lands before fallback removal); T-003 is correctly independent (separate subsystem, no API contract change) and parallel-safe. No circular dependencies; all `dependsOn` reference existing lower-priority IDs.

<promise>PASS</promise>
