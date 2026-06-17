## Context

Issue Detail (`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`) is the page users open to act on a running workflow, but the runtime answer is currently spread across several regions:

- Header pills: `WorkflowStagePill`, `HealthPill`, a `Running` pill, and an `Approval needed` pill (`IssueDetailPage.tsx:428-449`).
- `WorkflowView` (`packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx`): `StageBar` tabs, `StepList` task/check rows, `InlineApproval`, `SpecialStatePanel` (backlog/blocked/interrupted), `IntegrateFailurePanel`.
- The right-hand `CardSection title="Actions"` (`IssueDetailPage.tsx:843-1076`): Start, Close, Force Stop, Retry, Resume, Rerun, Stop.
- Separate supporting panels: `WorkflowConvergencePanel`, the Base Drift card, the Interrupted card, `LatestArtifactsPanel`, `TaskProgressPanel`, `WorkflowSessionsPanel`.

Transport notices reach the page via the SignalR `events-hub` (`packages/web/src/shared/api/events-hub.ts`) and the live-events provider (`packages/web/src/app/providers/LiveTaskProvider.tsx`); the hub currently only `console.error`s on failure and there is **no toast/notification library** in `packages/web`, so disconnect text has leaked inline between content sections.

All facts needed to answer "what must I do next?" are already on the client: `issue.workflowStage`, `issue.health`, `issue.approvalState`, `issue.recovery` (`currentWorkItem`, `latestAttemptState`, `allowedActions`), `issue.convergence`, `issue.drift`, `issue.startEligibility`, `issue.workflowStageProgress` (`currentTaskTitle`), `workflowTimeline` (stages/tasks/checks/`availableActions`/`pendingWork`), and `agentStatus` (`activeAgents`, `runnerAvailable`, `capacity`). There is **no backend, API, read-model, workflow-execution, or approval-policy work** in this change (those gaps remain tracked by #21, #23, #36).

## Goals / Non-Goals

**Goals:**
- One primary runtime decision surface near the top of Issue Detail that resolves to exactly one of `running`, `queued`, `approval required`, `blocked`, `failed`, `done`.
- The surface names the current task/check + status next to the required next action.
- Consolidate approval / recovery / safe-inspection / start / wait actions into the surface, driven by existing API facts.
- Keep sessions and logs as supporting evidence; the surface alone answers wait / approve / recover.
- Route runtime transport notices to Logs / Activity / toast / debug — never inline between issue content sections.
- Regression tests for running, approval-required, and disconnected-runtime-notice rendering.

**Non-Goals:**
- Fixing read-model gaps (#21 active leases, #23 queued state, #36 task classification).
- Redesigning the Markdown Reader.
- Changing workflow execution semantics or approval policy.
- Removing the stage bar, task/check detail, sessions, or content sections — they stay as supporting detail beneath the surface.

## Decisions

### Decision 1: New `RuntimeDecisionSurface` component + pure `deriveRuntimeDecision()` helper

Introduce `packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx` and a pure, dependency-free helper `deriveRuntimeDecision(input)` in `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`. The helper takes the already-fetched API facts and returns `{ summary, currentTask, nextAction, actions }`. The component only renders what the helper returns.

**Rationale:** Keeping derivation pure and component-free makes the precedence rules unit-testable without React/DOM, and lets the existing page pass in data it already fetches (no new queries).
**Alternative considered:** Derive inline inside the component with `useMemo`. Rejected — precedence logic is the riskiest part of this change and deserves isolated unit tests.

### Decision 2: Fixed state-precedence order

`deriveRuntimeDecision()` evaluates signals in this order and returns the first that matches:

1. `done` — `workflowStage === Done` or `status === Done` or `health === Done`.
2. `failed` — a Check-stage failed health/script check that blocks approval **takes precedence over** `approval required` (matches the existing "Full verification failed" gate in `WorkflowView.tsx:964`); otherwise `recovery.latestAttemptState === 'failed'`.
3. `approval required` — `approvalState.status === 'awaiting'` and not failed-verification-blocked.
4. `blocked` — `health === Blocked`, or `recovery.latestAttemptState` indicating a blocked/non-recoverable state, or unresolved `convergence` (`unresolvedItemIds.length > 0`).
5. `queued` — work is intended to run but waiting: `startEligibility.waitingForCompletion`, `runnerAvailable === false`, capacity full, or a pending lease/queue signal — while the issue is not backlog-idle.
6. `running` — `isAgentRunningOnThis`, or `health === Active` / `recovery.latestAttemptState === 'running'`.

**Rationale:** Mirrors the issue's six required states and the observed bug where `approvalState=awaiting` plus a failed Check verification must read as "failed", not "approval required".
**Alternative considered:** Let approval always win. Rejected — it would tell users to approve when verification has failed.
**`queued` graceful degradation:** Because the queued-state read model (#23) is out of scope, "queued" is only shown when an explicit queue/wait signal is present; when absent, the helper falls back to `running` or an idle/backlog state rather than guessing. This is documented as an explicit dependency on #23.

### Decision 3: Current task/check naming source

Prefer `recovery.currentWorkItem` (gives `type` task/check + `title`); fall back to `workflowStageProgress.currentTaskTitle`; fall back to the first `running` task/check in the current `workflowTimeline` stage; finally fall back to the stage name. This satisfies "names the current task/check" without new API fields.

### Decision 4: Surface placement; demote, do not delete, existing panels

Render `<RuntimeDecisionSurface issue={issue} timeline={timeline} agentStatus={agentStatus} />` directly above `<WorkflowView />` in `IssueDetailPage.tsx`. The header pills, stage bar, task/check rows, drift/convergence/interrupted cards, and sessions panels **remain visible beneath as supporting detail**. The right-hand Actions card and `InlineApproval` stop being the *primary* place to look — their controls are mirrored into the surface.

**Rationale:** Lowest-risk integration; preserves the detail users already rely on; satisfies "supporting evidence remains accessible".
**Alternative considered:** Delete the Actions card and InlineApproval entirely. Rejected — too disruptive in one change and risks losing edge-case controls; consolidation can harden later.

### Decision 5: Reuse existing mutation hooks for surface actions

The surface calls the same `approveIssue` / `rejectIssue` / `retryIssue` / `resumeIssue` / `rerunIssue` / `stopIssue` / `startIssue` mutations already wired in the page. Action enablement follows `recovery.allowedActions` ∪ `workflowTimeline.availableActions` (already computed at `IssueDetailPage.tsx:404-408`), never issue-status heuristics alone.

### Decision 6: Transport-notice routing via a lightweight toast host + connection-state hook

1. Add a tiny toast host (`packages/web/src/shared/ui/toast/`) — no external dependency — plus a `useConnectionState(projectId)` hook in `events-hub.ts` that exposes `connecting | connected | reconnecting | disconnected` from the existing `HubConnection` (`connection.onreconnecting`, `connection.onreconnected`, `connection.onclose`).
2. The host renders transport notices (disconnect/reconnect/runner-drop) as toasts and mirrors them to the existing Activity surface; it never injects text into Issue Detail content.
3. Audit the live-events → page render path so that any message previously rendered inline between Commits/Comments/Description is either dropped from content or redirected to the toast/Activity channel.

**Rationale:** The issue names "Logs/Activity/toasts or a debug panel"; a minimal hand-rolled toast avoids adding a dependency and gives one routing target. The connection state already exists on the SignalR connection but is currently unexposed.
**Alternative considered:** Adopt `sonner`/`react-hot-toast`. Rejected for now — the notice volume is tiny (disconnect/reconnect) and a 60-line host avoids a new dep and a license review; a library can replace it later without changing the routing contract.

## Risks / Trade-offs

- [Duplicate competing actions if the surface and legacy Actions card both render the same controls prominently] -> The surface is the single primary answer; the legacy Actions card stays but is visually demoted to supporting detail. Accept the transient redundancy rather than deleting controls in one step.
- [State-precedence edge cases (e.g. interrupted-vs-failed, drift-needs-attention)] -> `deriveRuntimeDecision()` is pure and fully unit-tested; precedence is documented in Decision 2. Interrupted maps to `blocked`/recovery guidance; drift `needs-attention` surfaces as a secondary note without overriding the primary summary.
- [`queued` cannot be reliably derived until #23 lands] -> Graceful fallback to `running`/idle when no explicit queue signal exists; documented as an explicit dependency. No false "queued" is shown.
- [Toast host scope creep] -> Constrain the initial host to transport/runner notices only; product toasts are a separate future concern.
- [Inline transport text rendered by a path not reached by the audit] -> Add a component test that renders Issue Detail over a disconnected fixture and asserts no inline transport text appears between content sections (direct AC coverage).

## Migration Plan

- **Deploy:** Frontend-only change behind the normal web build; no server, API, or data migration. Optionally gate the surface with a feature flag during hardening, then remove the flag.
- **Verify:** `npm run dev:web` manual check of running / approval-required / blocked / failed / done issues; run the new component and page tests.
- **Rollback:** Revert the web commits; the legacy header pills, Actions card, and InlineApproval remain intact (they were demoted, not removed), so rollback restores the prior experience with no data impact.

## Open Questions

- Should the legacy Actions card be removed entirely once the surface hardens, or kept indefinitely as a secondary control cluster? (Proposed: revisit after one cycle of usage.)
- Is a hand-rolled toast host acceptable, or does the team prefer adopting a library now to avoid a future migration? (Default: hand-rolled, constrained to transport notices.)
- For "queued", should we surface the specific wait reason (prerequisite / runner / capacity / lease) distinctly, or keep a single `queued` label until #23 provides structured queue data?
