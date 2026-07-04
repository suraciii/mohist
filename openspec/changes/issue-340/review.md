# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`, server workflow action contract
  Evidence: Normal running issues can lose the enabled Stop action. `deriveRuntimeDecision` only enables Stop from `issue.recovery.allowedActions` or `workflowTimeline.availableActions` (`derive-runtime-decision.ts:160-196`), but the current issue read model sent by the server has no `recovery` property (`IssueReadModel.cs:10-56`) and `WorkflowStatusMapper.BuildAvailableActions` does not emit any Stop action for running workflows (`WorkflowStatusMapper.cs:140-185`). The reducer therefore falls back to a disabled Stop with `Stop is not currently offered by the backend projection` for the live API shape, breaking the acceptance criteria for a usable single Stop entry and unchanged stop behavior. [disallowed:reason] Repair requires changing the runtime action contract or decision model behavior, not a small local review fix.
  SuggestedAction: Derive running Stop availability from the actual current backend contract, or update the server projection/read model to expose the stop/force-stop availability that the reducer expects. Add a test using the real API-shaped issue read model with no `recovery` field and a running timeline with no Stop action.
  Verification: Code inspection of `derive-runtime-decision.ts`, `IssueReadModel.cs`, and `WorkflowStatusMapper.cs`; `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed but does not cover this API-shaped running case.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`
  Evidence: A running issue can be classified as `queued` whenever global runner capacity is full, even if the active agent is running this same issue. `determineSummary` calls `buildWaitReason` and returns `queued` for `capacity.active >= capacity.max` at `derive-runtime-decision.ts:462-471` before it checks `input.hasActiveAgent` at `derive-runtime-decision.ts:478-480`. On a common 1/1-capacity system, the running issue's own agent fills capacity, so the primary runtime surface can stop showing the running state and Stop action. [disallowed:reason] Repair requires changing state precedence in the runtime decision model.
  SuggestedAction: Classify current issue activity before applying capacity wait reasons, or make capacity gating apply only to backlog/startable issues that are not already active.
  Verification: Code inspection of the reducer ordering; existing capacity tests cover backlog Start gating but not `hasActiveAgent: true` with full capacity.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`, approval send-back behavior
  Evidence: Send back is disabled for the server's current timeline action name. The server emits `request-changes` for awaiting approval (`WorkflowStatusMapper.cs:144-148`), while `actionEnabled(..., 'send-back')` only accepts `reject`, `send-back`, or `send_back` (`derive-runtime-decision.ts:183-185`). Since the detail page mounts `WorkflowView` as read-only (`IssueDetailPage.tsx:272-274`), the old request-changes controls are intentionally suppressed, so the user can see only a disabled Send back action even though the backend offers it. [disallowed:reason] Repair changes product behavior/action mapping.
  SuggestedAction: Recognize `request-changes` as the backend action for the send-back runtime action, or align the server projection name with the reducer. Add reducer and page tests for the server-emitted `request-changes` action.
  Verification: Code inspection of `WorkflowStatusMapper.cs`, `derive-runtime-decision.ts`, and `IssueDetailPage.tsx`; web tests pass but use `reject` in the new reducer/page tests.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`, `packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx`
  Evidence: Stop recoverability is inferred incorrectly from the action name `stop`. `hasRecoverableStop` treats either `stop` or `force-stop` as recoverable (`derive-runtime-decision.ts:166-171`), and the surface dispatches `forceStopMutation` when `decision.stopRecoverable` is true (`RuntimeDecisionSurface.tsx:256-265`). The server contract states the opposite endpoint semantics: `/force-stop` preserves progress and can resume, while `/stop` is terminal and cannot resume (`IssueRoutes.WorkflowControl.cs:160-180`). If the projection exposes terminal `stop`, the UI shows recoverable confirmation copy and calls the resumable endpoint. [disallowed:reason] Repair requires deciding the frontend/backend action vocabulary for destructive stop semantics.
  SuggestedAction: Treat only the explicit recoverable backend action as recoverable, and treat terminal `stop` as irreversible. Add tests that use the real endpoint/action names rather than assuming `stop` is recoverable.
  Verification: Code inspection of reducer, runtime surface, and server route comments/handlers.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx`, issue-detail workflow evidence
  Evidence: Making the embedded `WorkflowView` read-only suppresses write controls, but it also blocks evidence navigation. `IssueDetailPage` passes `readOnly` (`IssueDetailPage.tsx:272-274`); `WorkflowView` ignores stage selection in read-only mode (`WorkflowView.tsx:1418-1424`), and task rows disable expansion in read-only mode (`WorkflowView.tsx:532-536`). The expanded content contains task output, required files, failure guidance, and artifact details (`WorkflowView.tsx:578-619`), so the detail page no longer preserves readable workflow evidence as intended by the design. [disallowed:reason] Repair requires changing component semantics beyond a small local guard.
  SuggestedAction: Split `readOnly` into `suppressWriteControls` and evidence interactivity, or otherwise allow stage selection/task expansion while hiding mutation controls.
  Verification: Code inspection of `IssueDetailPage.tsx` and `WorkflowView.tsx`; no test covers read-only evidence expansion or stage selection.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: issue-detail visual substrate
  Evidence: Visible issue-detail surfaces still use hardcoded light palettes and white blocks, so the theme-token/dark-mode acceptance criterion is not met. `BranchBar`, rendered directly by the detail page (`IssueDetailPage.tsx:264-270`), still uses classes such as `border-blue-200 bg-blue-50` (`BranchBar.tsx:67`), `border-amber-200 bg-amber-50` (`BranchBar.tsx:134`), and `bg-red-50 text-red-600` (`BranchBar.tsx:173-180`). `TaskProgressPanel`, rendered in the right rail (`IssueDetailPage.tsx:390-399`), still uses `border-red-200`, `hover:bg-red-50`, `bg-red-50/50`, `bg-slate-50/50`, and `bg-white` in failure/evidence panels (`TaskProgressPanel.tsx:68-107`). [disallowed:reason] Repair requires a broader visual migration across mounted workflow widgets.
  SuggestedAction: Migrate the issue-detail-mounted workflow widgets to semantic tokens/CardSection styling, or replace them with evidence-only surfaces that share the detail page card substrate.
  Verification: Code inspection and regex search for raw Tailwind palettes under issue-detail-mounted workflow UI.
  Status: open

- [ID: item-7]
  Severity: minor
  Scope: runtime status badges
  Evidence: Runtime state is still rendered twice as a badge-like visual. The header renders `RuntimeSummaryPill` in the `status-badges-runtime` group (`IssueDetailPage.tsx:168-170`), while `RuntimeDecisionSurface` renders another pill-style `runtime-summary-label` inside the primary runtime surface (`RuntimeDecisionSurface.tsx:303-311`). This conflicts with the acceptance criterion that runtime badges should show only the current situation as one grouped running-state badge. [disallowed:reason] Repair requires product/visual judgment about which surface owns the visible runtime label.
  SuggestedAction: Keep one runtime state badge and make the other surface rely on headline/tone/icon without duplicating badge semantics.
  Verification: Code inspection of `IssueDetailPage.tsx` and `RuntimeDecisionSurface.tsx`.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: `packages/web` regression coverage
  Evidence: The web suite passes, but the tests miss the API-contract cases behind the blocking bugs: running issue with no `recovery` field and no timeline Stop action, `request-changes` as the approval send-back action name, current issue active while capacity is full, and read-only workflow evidence expansion. Existing tests instead inject `recovery.allowedActions` with `stop`/`reject` (`derive-runtime-decision.test.ts:34-39`, `derive-runtime-decision.test.ts:108-135`, `IssueDetailPage.test.tsx:286-333`) even though those fields/action names are not what the current server read paths provide. [disallowed:reason] Repair requires adding product regression tests and likely changing behavior first.
  SuggestedAction: Add reducer/page tests that mirror the server read model and timeline action names, plus a read-only evidence interaction test for `WorkflowView`.
  Verification: `npm run test:run -w packages/web` passed with `258` files and `4034` tests, confirming the gap is not detected by the current suite.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: server/frontend type contract drift
  Evidence: The TypeScript `Issue` model includes optional `recovery`, `convergence`, and `drift` fields (`issue.ts:116-119`), but the current server `IssueReadModel` does not expose those fields (`IssueReadModel.cs:10-56`). This drift likely predates parts of this issue, but the candidate now relies on those optional fields for runtime-state decisions.
  SuggestedAction: Align generated/read model contracts or remove dead frontend assumptions so future UI logic is not built on absent API data.
  Status: pre-existing

<promise>FAIL</promise>
