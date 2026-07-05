### Requirement: Per-summary presentation structure

The four presentation builders (`buildHeadline`, `buildRationale`, `buildNextAction`, `buildActions`) SHALL be reorganized from the **per-builder** dimension (each builder walking every `RuntimeSummary` via `if (summary === …)` chains) to the **per-summary** dimension. For each `RuntimeSummary` value, its full presentation — headline, rationale, nextAction, and action set — MUST co-reside in one place (e.g. a `Record<RuntimeSummary, { headline, rationale, nextAction, actions }>` table or a per-summary strategy object). Adding or modifying a single summary state's presentation MUST touch exactly one location, not four.

#### Scenario: Adding or modifying one summary touches one place

- **WHEN** a maintainer changes the headline, rationale, nextAction, or action set for a single `RuntimeSummary` value
- **THEN** the change is made in that summary's single co-located presentation entry, with no requirement to edit any per-builder function that walks all summaries

#### Scenario: No per-builder summary walks remain

- **WHEN** the presentation module is inspected for functions that branch over every `RuntimeSummary` value to produce one projection (headline, rationale, nextAction, or actions)
- **THEN** no such per-builder `if`/`switch` walk over all summaries exists; each projection is reached through the per-summary structure

### Requirement: Output contract preserved verbatim

The `RuntimeDecision` produced by `deriveRuntimeDecision` for any `RuntimeDecisionInput` SHALL be byte-for-byte identical to before this change, across every `RuntimeSummary` value and every combination of issue/timeline/agent inputs. The reorganization is structural only. The `RuntimeDecision` interface (fields and types), the `RuntimeSummary` enum value set (`running`, `queued`, `approval-required`, `blocked`, `failed`, `done`), the `RuntimeActionKind` set, the action labels, the action ordering, the `enabled`/`reason` resolution rules, the primary-action selection, the `stopRecoverable` flag, the `waitReason`, the `driftNote`, the `blockedReason`, and the `approvalStage` MUST all be preserved unchanged.

#### Scenario: Classification into RuntimeSummary is unchanged

- **WHEN** `deriveRuntimeDecision` is invoked with any combination of issue status, workflow stage/status, health, approval state, blocked reason, recovery projection, convergence, drift, stage progress, prerequisites, draft/canStart/blocker flags, timeline, agent status, and active-agent signals
- **THEN** the selected `RuntimeSummary` is identical to before this change, including all precedence rules (done → failed-script-health-check override → recovery failed → approval-awaiting → blocked/convergence/interrupted → queued → running → awaiting-approval fallback → check-stage failed health → default running)

#### Scenario: Headline, rationale, and nextAction are unchanged

- **WHEN** `deriveRuntimeDecision` produces a headline, rationale, or nextAction for any summary and any current-task / wait-reason combination
- **THEN** each string matches the pre-refactor output exactly, including the stage-label formatting, current-task title interpolation, blocked-reason passthrough, convergence blocked-reason passthrough, interrupted-reason fallback, wait-reason fallback for queued, and the per-action enabling order used by nextAction

#### Scenario: Action set, ordering, and enablement are unchanged

- **WHEN** `deriveRuntimeDecision` builds the `actions` array for any summary (approval-required, failed, blocked, queued, running, done, or backlog)
- **THEN** the same actions appear in the same order with the same labels, the same `enabled` flags, the same `reason` strings, the same `!isClosed` / `!isDone` gating, the same failed-vs-blocked divergence (Start new workflow vs Stop), the same backlog Start gating, the same disabled inspect action, and the same primary-action selection rule (first enabled non-inspect, else first non-inspect)

#### Scenario: Stop recoverability, drift, and secondary notes are unchanged

- **WHEN** `deriveRuntimeDecision` resolves `stopRecoverable`, `waitReason`, `driftNote`, `blockedReason`, or `approvalStage`
- **THEN** each value matches the pre-refactor output exactly, including the recoverable-stop detection over `recovery.allowedActions` (`stop` / `force-stop` / `force_stop`), the drift-note precedence (`nextAction` → `needs-attention` → `defer` → default), and the interrupted-reason assembly

### Requirement: Shared query helpers become reusable

The pure input-query helpers (`findRunningTask`, `findFailedCheck`, `findRunningCheck`, `isScriptHealthCheck`, `formatStageLabel`) SHALL be extracted into a reusable module so that both the WorkflowView and the decision derivation consume the same implementation rather than each maintaining its own copy. The `findFailedScriptHealthCheck` helper (used during classification) and any other shared query logic MUST NOT be duplicated across the view and the model.

#### Scenario: Single source for shared query helpers

- **WHEN** the codebase is inspected for the implementation of `findRunningTask`, `findFailedCheck`, `findRunningCheck`, `isScriptHealthCheck`, and `formatStageLabel`
- **THEN** each helper is defined exactly once in the shared query-helpers module and imported by every former call site (the view and the decision derivation), with no surviving private duplicate

#### Scenario: Shared helpers behave identically to before

- **WHEN** a shared query helper is invoked with any timeline shape (missing, empty, nested tasks/checks, mixed statuses, script-health vs ordinary checks)
- **THEN** its return value matches the pre-refactor implementation exactly, including null/empty handling, case-insensitive status comparison, title/name fallback, and stage-label title-casing over `_`/`-` separators

### Requirement: Classifier and pure-input separation maintained

The classification logic (`determineSummary`) and the pure-input queries it depends on SHALL remain separated from the presentation projection. Classification SHALL remain a single function returning a `RuntimeSummary`; presentation SHALL remain a per-summary lookup over that result. The reorganization MUST NOT re-merge classification with presentation or fold the pure-input queries back into the presentation module.

#### Scenario: Classification stays a single summary selector

- **WHEN** the model module is inspected for how a `RuntimeSummary` is chosen
- **THEN** a single classifier (`determineSummary` or equivalent) produces the summary, and the presentation layer consumes that summary without re-deriving it

### Requirement: Regression guards pass unchanged

The existing regression guard (`model/derive-runtime-decision.test.ts`) SHALL pass without modification. The per-summary reorganization is logic-equivalent, so no new tests are required to cover it; the existing test matrix (classification precedence, current-task fallbacks, action availability from projections, drift notes) is the equivalence oracle. `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` MUST both pass after the reorganization.

#### Scenario: Existing derivation tests pass

- **WHEN** `npm run test:run -w packages/web` runs the `derive-runtime-decision.test.ts` suite
- **THEN** every scenario (running, done, approval-required, failed-script-health override, queued variants, blocked variants, current-task fallback chain, action availability matrix, primary-action selection, drift notes) passes without edits to the test file

### Requirement: No enum or contract additions

This capability MUST NOT add or remove any `RuntimeSummary` enum value, MUST NOT add or remove any `RuntimeActionKind`, MUST NOT change the `RuntimeDecision` interface shape, and MUST NOT change the `RuntimeDecisionInput` shape. It is strictly a structural reorganization of existing presentation logic.

#### Scenario: Public types are unchanged

- **WHEN** the exported types from the decision-derivation module are compared before and after this change
- **THEN** `RuntimeSummary`, `RuntimeActionKind`, `RuntimeCurrentTask`, `RuntimeAvailableAction`, `RuntimeDecision`, and `RuntimeDecisionInput` are byte-for-byte identical
