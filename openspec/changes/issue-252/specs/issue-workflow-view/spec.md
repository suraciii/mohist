### Requirement: One-panel-per-file module boundaries

The issue-workflow widget's `ui/` directory SHALL honor the existing "one panel per file" convention already followed by `TaskProgressPanel`, `WorkflowSessionsPanel`, and `RuntimeDecisionSurface`. The `WorkflowView` god component MUST be decomposed so that each cohesive group of helpers, icons, and subcomponents lives in its own sibling file under `ui/`, and `ui/WorkflowView.tsx` MUST shrink to composition/assembly only (mounting the stage bar, special-state panel, step list, and integrate-failure panel in the same order with the same conditional rules as before this change).

The target file decomposition SHALL be:

- `ui/format.ts` — pure helpers (`classifyResult`, `formatDuration`, `formatClock`, `parseTimelineTaskOutput`, origin-label formatters).
- `ui/StageStatusIcons.tsx` — the six status icons (`CheckmarkIcon`, `CrossIcon`, `SpinnerIcon`, `EmptyCircleIcon`, `HourglassIcon`, `InterruptedIcon`) plus the `StageStatusIcon` dispatcher.
- `ui/StageBar.tsx` — `StageBar`, `StageBarCell`, and stage helpers (`getStageStatus`, `getStageDuration`, `workflowTimelineToStageStateMap`).
- `ui/TaskItem.tsx` — `TaskItem`, `TaskLifecycleTime`, `RunningElapsed`, `TaskSessionChip`, `TaskArtifactSummaryChip`, `RequiredFileEntry`.
- `ui/CheckItem.tsx` — `CheckItem`, `CheckRepairPanel`.
- `ui/InlineApproval.tsx` — `InlineApprovalControls`, `StepList`.
- `ui/failure-panels.tsx` — `DeliveryFailureBanner`, `IntegrateFailurePanel`, `SpecialStatePanel`.
- `ui/WorkflowView.tsx` (slimmed) — composition only.

#### Scenario: WorkflowView becomes a composition-only entry

- **WHEN** the codebase is inspected for inline definitions of stage-bar, task-item, check-item, inline-approval, step-list, delivery-failure, integrate-failure, special-state, status-icon, and pure-formatting helpers inside `ui/WorkflowView.tsx`
- **THEN** none of those definitions appear inline in `WorkflowView.tsx`; each is imported from its dedicated sibling file, and `WorkflowView.tsx` only assembles the panels in the prior conditional order

#### Scenario: Each target file owns its assigned exports

- **WHEN** the codebase is inspected for where each helper/subcomponent named in the target-file table is defined
- **THEN** each one is defined in exactly the file the table prescribes (e.g. `getStageStatus` lives in `ui/StageBar.tsx`, `CheckRepairPanel` lives in `ui/CheckItem.tsx`, `SpecialStatePanel` lives in `ui/failure-panels.tsx`)

### Requirement: Visual rendering preserved verbatim

The refactor MUST NOT alter the rendered DOM structure, copy, class names, interactive behavior, test-ids, keyboard handling, or conditional visibility of the workflow view. Byte-for-byte equivalence of the user-facing surface SHALL be preserved; only file boundaries move.

#### Scenario: Stage bar renders identically across breakpoints

- **WHEN** the stage bar is rendered for any combination of selected stage, stage status, stage duration, `readOnly`, and mobile/desktop breakpoint
- **THEN** the DOM, status icon dispatch, stage labels, duration formatting, arrow separators, click handling, `data-testid` (`workflow-stage-bar` / `workflow-stage-bar-scrollable-stepper`), and disabled/pending/selected styling are identical to before this change

#### Scenario: Task and check rows render identically

- **WHEN** a task or check is rendered for any status (pending, running, completed/passed, failed/error, awaiting-approval), with or without artifacts, required files, session chips, origin labels, reasons, health output, or delivery-failure guidance
- **THEN** the row's icon, title, chips, lifecycle time formatting (including the running-elapsed ticker), expand/collapse behavior, artifact viewer invocation, and attempt labels are identical to before this change

#### Scenario: Inline approval and request-changes flow unchanged

- **WHEN** inline approval controls render for any stage (Plan/Build/Check/Integrate) with or without approval output
- **THEN** the approve button label, request-changes form, feedback submission call (`requestChangesIssue` with `{ stage, body }`), success/error invalidation, "View Full Report" / "View Changes" affordances, and review-summary rendering are identical to before this change

#### Scenario: Failure and special-state panels unchanged

- **WHEN** the delivery-failure banner, integrate-failure panel, or special-state (backlog / blocked / interrupted) panel renders
- **THEN** the failure-kind color mapping, branch-invariant evidence block, workspace-setup attribution block, blocked/interrupted copy, Start/Resume button behavior, and the conditional gating (`workflowStage === Integrate`, `health === Blocked|Interrupted`) are identical to before this change

### Requirement: Widget barrel public surface unchanged

The widget's public barrel (`packages/web/src/widgets/issue-workflow/index.ts`) SHALL export exactly the same set of names with the same types as before this change. External consumers MUST require zero edits. Any export that previously reached outside the widget through the barrel (including `WorkflowView`, `CheckRepairPanel`, and the `deriveRuntimeDecision` family) MUST remain reachable unchanged.

#### Scenario: External imports resolve unchanged

- **WHEN** an external consumer imports any name from the issue-workflow widget barrel that was available before this change
- **THEN** the import resolves to the same symbol with the same type, regardless of which internal `ui/` or `model/` file now hosts its implementation

### Requirement: Regression guards pass unchanged

The existing regression guards (`ui/WorkflowView.test.tsx` and any sibling panel tests) SHALL pass without modification. No new public behavior is introduced by this capability, so no new tests are required to cover it; the existing tests are the equivalence oracle.

#### Scenario: Existing WorkflowView tests pass

- **WHEN** `npm run test:run -w packages/web` runs the `WorkflowView.test.tsx` suite
- **THEN** every scenario (timeline rendering, mobile stepper, approval/request-changes flow, feedback history, artifact chips, read-only gating) passes without edits to the test file

### Requirement: No behavioral or data-model additions

This capability MUST NOT introduce new runtime states, new actions, new approvals, new retry behavior, new visual layouts, or changes to the runtime data structures the view consumes. It is strictly a file-boundary refactor.

#### Scenario: No new behavior leaks in with the moved code

- **WHEN** the moved helpers and components are exercised through the composition entry
- **THEN** their behavior matches the pre-refactor behavior exactly, with no newly accepted props, no newly rendered elements, and no newly triggered side effects
