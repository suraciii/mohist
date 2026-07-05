## Context

The `issue-workflow` widget concentrates two high-density code spots that fight the directory's own "one panel per file" convention (already followed by `TaskProgressPanel`, `WorkflowSessionsPanel`, `RuntimeDecisionSurface`). Both are pure structural debt — no behavior change is intended — but each forces future presentation edits to land inside a monolith.

Verified current state (code):

- **`ui/WorkflowView.tsx` is 1455 lines.** The actual `WorkflowView` composition entry is only ~60 lines (`ui/WorkflowView.tsx:1396-1455`): it mounts `StageBar`, conditionally `SpecialStatePanel`, `StepList`, and `IntegrateFailurePanel`. Everything above line 1396 is inline — ~29 `function` definitions spanning four cohorts:
  - **Pure helpers** (`:25-56`): `classifyResult`, `formatDuration`, `parseTimelineTaskOutput`, `formatClock`.
  - **Status icons** (`:189-271`): `CheckmarkIcon`, `CrossIcon`, `SpinnerIcon`, `EmptyCircleIcon`, `HourglassIcon`, `InterruptedIcon`, plus the `StageStatusIcon` dispatcher.
  - **Stage helpers + cell** (`:101-189`, `:273-368`): `getStageStatus`, `getStageDuration`, `workflowTimelineToStageStateMap`, `StageBarCell`, `StageBar`.
  - **Peer subcomponents** (`:58-100`, `:370-1395`): `TaskLifecycleTime`, `RunningElapsed`, `RequiredFileEntry`, `TaskSessionChip`, `TaskArtifactSummaryChip`, `TaskItem`, `isScriptHealthCheck`, `isDeliveryFailureTask`, `DeliveryFailureBanner`, `CheckItem`, `formatOriginLabel`, `formatOriginTitle`, `InlineApprovalControls`, `StepList`, `SpecialStatePanel`, `IntegrateFailurePanel`.
- **`model/derive-runtime-decision.ts` is 673 lines.** It is already split into well-named functions, but the four presentation builders — `buildHeadline` (`:495`), `buildRationale` (`:538`), `buildNextAction` (`:578`), `buildActions` (`:204`) — each walk **every** `RuntimeSummary` via `if (summary === …)` chains. Adding one summary's copy means editing 4 functions. The classifier `determineSummary` (`:414`) and pure-input queries are already separated from presentation as functions; they just share one file.
- **Query-helper duplication is real.** `isScriptHealthCheck` is defined in both `ui/WorkflowView.tsx:636` and `model/derive-runtime-decision.ts:76`. The model also owns `findFailedScriptHealthCheck`, `findRunningCheck`, `findRunningTask`, `findFailedCheck`, `formatStageLabel` (`:88-159`); the view re-derives the same timeline queries inline. The spec requires a single source.
- **`RuntimeSummary` is a 6-value string union** (`running | queued | approval-required | blocked | failed | done`); `RuntimeActionKind` is 8 values. Both are frozen by spec — no additions or removals.
- **Test oracles exist and are the equivalence contract:** `ui/WorkflowView.test.tsx` (~774 lines, timeline/mobile/approval/request-changes/feedback/artifact/read-only scenarios) and `model/derive-runtime-decision.test.ts` (~734 lines, classification precedence, current-task fallbacks, action matrix, drift notes). Both must pass unchanged.
- **Widget barrel** (`index.ts`) re-exports `WorkflowView`, `deriveRuntimeDecision`, and the `Runtime*` types. External consumers must see an identical public surface.

Constraints / stakeholders: per `design/architecture.md` the view layer renders derived state; the model layer owns derivation. Per `design/testing.md`, no new external dependencies, no wall-clock, existing specs are the oracle. AGENTS.md states the project is in active development with no version-compatibility obligation, so a single coordinated refactor landing in one PR is acceptable.

## Goals / Non-Goals

**Goals:**
- Decompose `ui/WorkflowView.tsx` into the eight target files listed in the spec so the entry becomes composition-only; each helper/icon/subcomponent lives in exactly one prescribed sibling file.
- Reorganize the four presentation builders from per-builder summary walks into a per-summary structure where one state's headline + rationale + nextAction + actions co-reside, so a single-state edit touches one location.
- Extract the duplicated pure-input query helpers (`findRunningTask`, `findFailedCheck`, `findRunningCheck`, `isScriptHealthCheck`, `formatStageLabel`, `findFailedScriptHealthCheck`) into one reusable module consumed by both the view and the derivation.
- Preserve every output byte-for-byte: DOM, copy, class names, test-ids, action labels/ordering/enablement, `RuntimeDecision` fields, barrel exports.

**Non-Goals** (per proposal/spec):
- No new/removed `RuntimeSummary` or `RuntimeActionKind` values; no `RuntimeDecision`/`RuntimeDecisionInput` shape change.
- No change to available actions, approval/retry/stop behavior, or visual layout.
- No performance optimization; no new tests (existing suites are the oracle).
- No split of `derive-runtime-decision.ts` into multiple files beyond what the per-summary restructure and the query-helper extraction necessitate — the spec explicitly relaxes the original issue's "split the file" ask.

## Decisions

### D1. File decomposition follows the spec's target table verbatim; siblings are peers, not nested.

Target files under `ui/`:

| File | Owns |
|---|---|
| `ui/format.ts` | `classifyResult`, `formatDuration`, `formatClock`, `parseTimelineTaskOutput`, `formatOriginLabel`, `formatOriginTitle` |
| `ui/StageStatusIcons.tsx` | `CheckmarkIcon`, `CrossIcon`, `SpinnerIcon`, `EmptyCircleIcon`, `HourglassIcon`, `InterruptedIcon`, `StageStatusIcon` |
| `ui/StageBar.tsx` | `StageBar`, `StageBarCell`, `getStageStatus`, `getStageDuration`, `workflowTimelineToStageStateMap` |
| `ui/TaskItem.tsx` | `TaskItem`, `TaskLifecycleTime`, `RunningElapsed`, `TaskSessionChip`, `TaskArtifactSummaryChip`, `RequiredFileEntry` |
| `ui/CheckItem.tsx` | `CheckItem`, `CheckRepairPanel` |
| `ui/InlineApproval.tsx` | `InlineApprovalControls`, `StepList` |
| `ui/failure-panels.tsx` | `DeliveryFailureBanner`, `IntegrateFailurePanel`, `SpecialStatePanel` |
| `ui/WorkflowView.tsx` (slimmed) | composition only (`StageBar` → `SpecialStatePanel` → `StepList` → `IntegrateFailurePanel`) |

Cross-file dependencies follow the existing call graph: `StageBar` imports `StageStatusIcon` and `format` helpers; `TaskItem`/`CheckItem` import `format` + `StageStatusIcons`; `failure-panels` imports `format`. No file re-exports another's symbol — each consumer imports from the owning sibling, preserving the one-definition-per-symbol rule the spec scenario asserts.

Rationale: the table is prescribed by spec; the only design freedom is dependency direction, and "import from the owning sibling" keeps the graph acyclic and matches how `TaskProgressPanel`/`RuntimeDecisionSurface` already consume `format`-style helpers from their peers.

Alternatives considered: **a single `ui/internal.ts` barrel** re-exporting all siblings (rejected — hides the one-symbol-one-file invariant the spec scenario checks and recreates a mini-monolith); **nest subcomponents under a parent file** e.g. `CheckRepairPanel` inside `CheckItem` is already correct, but `StepList` under `InlineApproval` rather than its own file (rejected — spec pins `StepList` to `InlineApproval.tsx`, which we follow).

### D2. Shared query helpers land in `model/runtime-query-helpers.ts`; the view imports upward.

New module `model/runtime-query-helpers.ts` owns: `findRunningTask`, `findFailedCheck`, `findRunningCheck`, `findFailedScriptHealthCheck`, `isScriptHealthCheck`, `formatStageLabel`. Both `model/derive-runtime-decision.ts` and `ui/WorkflowView.tsx` (and its extracted siblings, e.g. `TaskItem`/`CheckItem`) import from there. The private duplicate `isScriptHealthCheck` in `WorkflowView.tsx:636` is deleted.

Rationale: these helpers are pure timeline/state queries — model-layer concerns — and `derive-runtime-decision` is their primary heavy user. The `ui/` layer already imports across the `ui/`→`model/` boundary (e.g. `WorkflowView` consumes `deriveRuntimeDecision`'s types), so importing query helpers from `model/` introduces no new layering direction. Co-locating with the derivation keeps the queries next to their canonical consumer and the `RuntimeDecisionInput` timeline shape they inspect.

Alternatives considered: **`ui/runtime-query-helpers.ts`** (rejected — flips layering: the model would import from `ui/`, and the helpers inspect model-layer timeline types); **a neutral `shared/` folder** (rejected — adds a third bucket for 6 functions; `model/` is already the home of timeline-inspecting logic); **keep in `derive-runtime-decision.ts` and re-export** (rejected — leaves the derivation file bloated and re-introduces a god-module at the model layer).

### D3. Per-summary presentation uses a `Record<RuntimeSummary, SummaryPresentation>` table whose entries are functions of a shared context — not static templates.

The four builders cannot become a table of *values* because each projection interpolates runtime data: `headline`/`rationale` need `currentTask.title` and `formatStageLabel(issue.workflowStage)`; `nextAction` reflects the first enabled non-inspect action; `actions` depends on `input`, `isBacklog`, `isClosed`, `isDone`. So each entry is a function over a shared `SummaryPresentationContext`:

```ts
type SummaryPresentationContext = {
  input: RuntimeDecisionInput
  issue: RuntimeDecisionInput['issue']
  currentTask: RuntimeCurrentTask | null
  isBacklog: boolean
  isClosed: boolean
  isDone: boolean
  allowed: Set<string>          // buildAllowedActions(input), computed once
  waitReason: string | null     // buildWaitReason(input), computed once
}

type SummaryPresentation = {
  headline: (ctx: SummaryPresentationContext) => string
  rationale: (ctx: SummaryPresentationContext) => string
  nextAction: (ctx: SummaryPresentationContext) => string
  actions: (ctx: SummaryPresentationContext) => RuntimeAvailableAction[]
}

const PRESENTATIONS: Record<RuntimeSummary, SummaryPresentation> = { ... }
```

`deriveRuntimeDecision` computes `summary` via `determineSummary`, builds the context once, then reads `PRESENTATIONS[summary]` for each projection. The existing `actionEnabled`/`pickPrimaryAction`/`pickCurrentTask`/`buildDriftNote`/`hasRecoverableStop` helpers stay as module-private utilities called from inside the per-summary action builders (or from `deriveRuntimeDecision` for cross-cutting fields like `stopRecoverable`/`driftNote`/`blockedReason`/`approvalStage`, which are **not** per-summary projections and remain computed centrally).

Rationale: a function-valued table is the only shape that co-locates a state's full copy + actions *without* changing the output (static templates cannot express the `currentTask` interpolation or the `allowed`-set gating). TypeScript enforces totality: `Record<RuntimeSummary, …>` fails to compile if a summary is missing, so the "one location per state" invariant is machine-checked.

Alternatives considered:
- **Per-summary strategy objects (classes/objects with methods)** — isomorphic to the function table; rejected as needless ceremony (no shared state, no polymorphism beyond the table key).
- **Static `Record<RuntimeSummary, { headline: string; … }>` with `%task%`/`%stage%` placeholders + a tiny interpolator** — rejected: the action set, enablement, `nextAction` derivation, and `currentTask.kind` branching ('Check' vs 'Task') cannot be expressed as string templates without recreating inline logic; placeholders would leak presentation logic into the caller.
- **Keep four builders but back them with per-summary data objects** — rejected: leaves the per-builder walk in place, which is the exact anti-pattern the spec scenario "No per-builder summary walks remain" forbids.

### D4. The `failed`/`blocked` shared action block is deduplicated via a module-private `terminalActions` helper invoked by both table entries — not by duplicating the block.

Today `buildActions` handles `failed` and `blocked` in one arm (`if (summary === 'failed' || summary === 'blocked')`) producing retry/resume/rerun/stop, then diverges: `failed` ends with Stop, `blocked` ends with Start (per spec scenario "failed-vs-blocked divergence"). Under the per-summary table each state needs its own `actions` entry. To avoid copy-paste, a module-private helper `terminalActions(ctx, { terminalKind })` builds the shared retry/resume/rerun prefix and the divergent tail; the `failed` and `blocked` entries call it with their respective tail kind.

Rationale: the spec prioritizes co-location ("one state's full copy + action set co-resides in one place") over DRY, but verbatim output equivalence is the harder constraint — duplicating a 4-action block risks silent drift on `reason` strings. A shared prefix helper called from each entry satisfies both: each entry is still the single touch-point for *its* state, and the shared prefix has one definition.

Alternatives considered: **duplicate the block into both entries** (rejected — two copies of label/reason strings invite drift, violating the output-contract scenario); **keep `failed`/`blocked` as one combined entry keyed by a derived flag** (rejected — breaks the one-`RuntimeSummary`-one-entry invariant and re-couples the two states).

### D5. Sequencing is risk-ordered: zero-risk extraction first, logic-equivalence restructure last.

1. Extract `ui/format.ts` + `ui/StageStatusIcons.tsx` (pure helpers + stateless icons). Zero behavioral surface.
2. Extract peer subcomponents: `StageBar` → `TaskItem` → `CheckItem` → `InlineApproval` → `failure-panels`. Pure code motion; each file's imports are rewired to siblings + `format` + `StageStatusIcons`.
3. Extract `model/runtime-query-helpers.ts`; rewire `derive-runtime-decision.ts` and the new `ui/` siblings to it; delete the view's private `isScriptHealthCheck` duplicate.
4. Restructure presentation: introduce `SummaryPresentationContext` + `PRESENTATIONS` table (D3/D4); replace the four builders' bodies with table lookups; keep `determineSummary`, `buildDriftNote`, and the cross-cutting resolvers in `deriveRuntimeDecision`.
5. Slim `WorkflowView.tsx` to composition-only (its body at `:1396-1455` is already composition — step 2 removes everything above it).

After **each** step run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`. The two oracle suites must stay green throughout; a red gate after step 4 isolates the only logic-equivalence risk to the restructure.

Rationale: the issue explicitly ranks steps by risk; front-loading zero-risk extraction means if the restructure (step 4) surfaces an equivalence bug, the refactor is already a net win and the bug is localized to one step's diff.

Alternatives considered: **big-bang single commit** (rejected — makes equivalence review impossible and loses the green-gate checkpoints); **restructure first, then extract** (rejected — the restructure is harder to review inside the 673-line file than after the query helpers are extracted).

## Risks / Trade-offs

- **[Per-summary restructure silently changes an output string or action ordering]** → mitigated by D3 (function-valued table preserves interpolation verbatim), D4 (shared prefix helper prevents failed/blocked drift), and the `derive-runtime-decision.test.ts` oracle (classification precedence, current-task fallbacks, action matrix, drift notes). Step 4 is isolated so a regression points at one diff.
- **[Missed cross-file import after extraction breaks the build or, worse, resolves to a stale re-export]** → mitigated by TypeScript (`verbatimModuleSyntax` + `TreatWarningsAsErrors`-equivalent strict checks) catching unresolved imports at the typecheck gate after each step; the one-symbol-one-file invariant is asserted by the spec scenario.
- **[`PRESENTATIONS` table totality gap — a summary missing from the record]** → mitigated by `Record<RuntimeSummary, …>` compile-time exhaustiveness; a missing key is a hard build failure, not a runtime hole.
- **[Query-helper extraction changes a helper's null/case-insensitive behavior]** → mitigated by moving the function bodies byte-for-byte (no rewrite) and by the `derive-runtime-decision.test.ts` scenarios covering missing/empty/nested/mixed timelines; the view's private duplicate is deleted only after its callers compile against the shared module.
- **[WorkflowView slim-down drops a conditional that gated a panel]** → mitigated by the `WorkflowView.test.tsx` scenarios covering read-only gating, backlog `StepList` suppression, and integrate-failure visibility; step 5 only deletes code above the existing composition body, which is untouched.
- **[Barrel surface drift]** → mitigated by leaving `index.ts` unedited; internal moves are invisible to the barrel because it already imports from `./ui/WorkflowView` and `./model/derive-runtime-decision`.

## Migration Plan

1. **Step 1 (D5.1):** create `ui/format.ts` and `ui/StageStatusIcons.tsx`; move the listed symbols verbatim; rewire `WorkflowView.tsx` imports. Verify: typecheck + `WorkflowView.test.tsx` green.
2. **Step 2 (D5.2):** create `ui/StageBar.tsx`, `ui/TaskItem.tsx`, `ui/CheckItem.tsx`, `ui/InlineApproval.tsx`, `ui/failure-panels.tsx` in dependency order; move symbols verbatim; rewire imports across siblings. Verify after each file: typecheck + `WorkflowView.test.tsx` green.
3. **Step 3 (D5.3):** create `model/runtime-query-helpers.ts`; move the six query helpers verbatim; rewire `derive-runtime-decision.ts` and the `ui/` consumers; delete `WorkflowView.tsx`'s private `isScriptHealthCheck`. Verify: typecheck + both oracle suites green.
4. **Step 4 (D5.4):** in `derive-runtime-decision.ts`, introduce `SummaryPresentationContext` + `PRESENTATIONS` table (D3) and the `terminalActions` helper (D4); replace `buildHeadline`/`buildRationale`/`buildNextAction`/`buildActions` bodies with `PRESENTATIONS[summary].*` lookups from `deriveRuntimeDecision`; keep `determineSummary`, `buildDriftNote`, and cross-cutting resolvers central. Verify: typecheck + `derive-runtime-decision.test.ts` green (this is the primary equivalence gate).
5. **Step 5 (D5.5):** confirm `WorkflowView.tsx` is composition-only (delete any leftover inline debris above the entry). Verify: typecheck + full `npm run test:run -w packages/web` green.
6. **Deploy:** single coordinated PR; `mo update server` is a no-op for this web-only refactor (no server/runner change). Web rebuild ships the new file layout.
7. **Rollback:** `git revert` the PR. No data, config, or persistence change is involved, so rollback is trivially clean — the barrel and public types are identical before and after.

Verification gates (after every step): `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`.

## Open Questions

- **Where exactly does `CheckRepairPanel` live?** The spec pins it to `ui/CheckItem.tsx`, and today it is inline near `CheckItem` (`WorkflowView.tsx:741` region). Confirmed: it moves with `CheckItem`. No open question — recorded to close the loop on the `isScriptHealthCheck` neighbor.
- **Should `SummaryPresentationContext` be exported for external reuse, or kept module-private?** Lean: **module-private**. No external consumer needs to construct a presentation context; exporting it would expand the public surface the spec freezes. Revisit only if a sibling widget later needs the same projections.
- **`formatStageLabel` ownership:** it is used by both the derivation (model) and could be used by the view's stage labels. Today the view has its own stage-label formatting inline. Lean: **move `formatStageLabel` into `runtime-query-helpers.ts` but do not aggressively rewire view-side stage labels in this change** — that is a separate cleanup; this change only deduplicates the query helpers the spec names.
- **Step 4 file split:** the spec relaxes the "split `derive-runtime-decision.ts` into files" ask. Lean: **keep the per-summary table and classifier in `derive-runtime-decision.ts`** (now slimmer after query-helper extraction), unless the file stays above ~500 lines post-extraction, in which case split `PRESENTATIONS` into `model/runtime-presentations.ts`. Decide at step 4 based on the post-extraction line count.
