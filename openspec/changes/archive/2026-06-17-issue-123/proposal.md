## Why

Issue detail is the page users open to decide what to do with a running workflow, but today the answer is spread across header pills, workflow stage tabs, task/check rows, an inline approval panel, a right-hand Actions card, sessions, and inline runtime-transport notices. For an active or approval-waiting issue, users must mentally reconcile several regions before knowing whether to act, wait, inspect a failure, or recover — and connection-disconnected noise has even appeared as inline content between Commits and Comments, making runtime transport look like issue content. A single decision surface at the top of the page is needed now so the primary question ("what must I do next?") is answered before any supporting detail.

## What Changes

- Add one primary runtime decision surface near the top of Issue Detail that presents a single current-state summary: `running`, `queued`, `approval required`, `blocked`, `failed`, or `done`.
- Show the current task/check and its status inside that same surface, next to the required next action, so users never infer state from sessions.
- Consolidate the context-specific next action into the surface: approval (approve / send back), recovery (retry / resume / rerun / stop), safe inspection (View files, View transcript), and start/wait guidance — instead of scattering these across header pills, the workflow step list, and a separate Actions card.
- Derive the surface state and action availability from the existing API facts (`workflowStage`, `health`, `approvalState`, `workflowTimeline`, `recovery` projection, `drift`, `convergence`, agent-status) — no read-model or execution-semantics changes.
- Route runtime infrastructure notices (connection disconnects, transport errors, runner-drop indicators) to Logs/Activity, toasts, or a debug area, and forbid them from rendering as plain inline content between Description, Commits, Comments, or other issue sections.
- Keep sessions and logs reachable as supporting evidence (links/transcripts), but demote them so users never need to inspect a session to decide whether to wait, approve, request changes, or recover.
- Preserve the existing stage bar, task/check detail, and content sections below the surface as supporting detail, not as the primary state answer.
- Add regression coverage for at least running, approval-required, and disconnected-runtime-notice rendering.

## Capabilities

### New Capabilities

- `issue-runtime-decision-surface`: The single primary runtime decision surface on Issue Detail — its one current-state summary (running / queued / approval required / blocked / failed / done), naming the current task/check alongside the required next action, context-specific approval/recovery/inspection action placement, sessions-as-supporting-evidence-only behavior, and the rule that runtime transport notices are routed away from inline issue content. Includes the test-coverage requirement for running, approval-required, and disconnected-runtime-notice cases.

### Modified Capabilities

- `web-ui`: Extends the existing Issue Detail presentation contract so that approval controls, recovery actions, blocked/interrupted/drift/convergence guidance, and the "needs attention" state are surfaced through the single decision surface rather than being split between the header pills, the workflow step list, the inline approval panel, and the right-hand Actions card; and adds the constraint that runtime transport notices do not render as inline content between issue sections.

## Impact

- Issue Detail page: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx` — header pills, the Actions `CardSection`, interrupted/blocked/drift cards, and the runtime-notice placement are reorganized behind a new top-of-page decision surface component.
- Issue workflow widget: `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx` (`StageBar`, `StepList`, `InlineApproval`, `SpecialStatePanel`, `IntegrateFailurePanel`) — these remain as supporting detail beneath the surface; their approval and special-state panels feed/defer to the decision surface instead of being the primary answer.
- New decision-surface component and a small state-derivation helper that maps existing API fields (`workflowStage`, `health`, `approvalState`, `workflowTimeline`, `recovery`, `drift`, `convergence`, agent-status) to the single summary label and action set.
- Runtime-notice handling: wherever disconnect/transport text currently reaches inline Issue Detail content (e.g. via the live-events provider/`packages/web/src/shared/api/events-hub.ts` and `packages/web/src/app/providers/LiveTaskProvider.tsx` consumers) is routed to Logs/Activity, a toast, or a debug surface instead of rendered between content sections.
- Tests: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.test.tsx` (and a new decision-surface test) covering running, approval-required, and disconnected-runtime-notice rendering.
- No backend, API, read-model, workflow-execution, or approval-policy changes (those gaps remain tracked by #21, #23, #36); no Markdown Reader redesign.
