# Design: ModelSelect inline variant chips

## Context

The model selector is the most-touched control across model configuration. Today model selection lives in the shared `packages/web/src/shared/ui/ModelSelect.tsx` (a single Popover, provider-grouped flat list, substring search, ↑↓/Enter keyboard nav, `size="default" | "compact"`). Three surfaces pick models:

1. `IssueModelSelector.tsx` — issue default model **and** per-stage overrides. **Note:** its default-model popover is bespoke (it does *not* use the shared `ModelSelect`); only its per-stage rows use the shared component. The bespoke popover adds recent-models (localStorage), loading/error states, a "Use default" override row, and fuzzysort search.
2. `AiSettingsSection.tsx` — project default model and per-stage overrides, both via the shared `ModelSelect`.
3. `CreateIssueDialog.tsx` — issue-creation model picker.

The issue references a `VariantPicker.tsx`, `variantListFor`/`resolveVariantAgainstModel` helpers, and a `coderModelVariants` data source as if they exist. **None of these are present in the web frontend today.** Variant data is not yet wired into the web layer; its delivery is prerequisite #238. This means the variant-aware UI must degrade gracefully: when no variant data is supplied, `ModelSelect` renders exactly as today (no chips).

Data shape (once #238 lands): three-level `provider → model → variants[]`; 2-4 variants per model; only ~21 of ~78 models expose variants.

## Goals / Non-Goals

**Goals:**
- Variant chips render inline on model rows inside the shared `ModelSelect`, so model + variant are chosen in one popover in one action.
- Variant capability is *discoverable* at the model-list level (you see which models have variants without selecting first).
- Keyboard flow extends to chips (↑↓ rows, → into chips, Enter, Esc) without breaking the existing model-only flow.
- Compact sizing works for per-stage rows; mobile (375px) keeps ≥44px tap targets and wraps 4-chip rows.
- All three surfaces migrate to the unified control.
- Zero behavior change for callers that don't pass variant props (backward compatible).

**Non-Goals:**
- Runner/server variant delivery (#238).
- Variant badges on Kanban cards / issue detail header.
- Unifying `IssueModelSelector`'s bespoke default-model popover into the shared `ModelSelect` beyond what chip rendering requires (recent-models/loading/fuzzysort stay as-is unless trivially portable).
- Agent editor page variant UI (inherits automatically if it shares `ModelSelect`).

## Decisions

### D1: Variant-awareness is additive, optional props on the shared `ModelSelect`

Extend `ModelSelectProps` with:
- `modelVariants?: Record<string, string[]>` — keyed by model id; absent/empty ⇒ no chips rendered.
- `valueVariant?: string | null` — the currently selected variant for `value`.
- `onChangeVariant?: (variant: string | null) => void` — reports a chip selection (model reported via existing `onChange`).

**Rationale / alternatives:**
- Keeps `onChange: (model: string) => void` unchanged → every existing caller keeps working with no edits.
- *Alternative considered:* fold variant into a composite `onChange({ model, variant })`. Rejected: breaks all callers and the issue text already names `valueVariant` as a separate prop, signalling the additive direction.
- *Alternative considered:* a single `onChange(model, variant?)`. Rejected: ambiguous for callers that ignore the second arg and harder to opt out of variant reporting.

Chip click → call `onChange(modelId)` **and** `onChangeVariant?.(variant)`, then close. Model-body click → `onChange(modelId)` **and** `onChangeVariant?.(null)` (clear any prior variant), then close.

### D2: Chips are an internal subcomponent `ModelVariantChips`, shared by both popovers

A small presentational component renders the chip row for one model given `{ variants, activeVariant, size, onFocusMove, onSelect }`. It is used:
- Inside the shared `ModelSelect` row.
- Inside `IssueModelSelector`'s bespoke default-model `ModelListItem` (so the bespoke popover also gains chips without a full rewrite).

**Rationale / alternatives:**
- `IssueModelSelector`'s default popover is bespoke and richer than `ModelSelect` (recent, loading, fuzzysort). Migrating it wholesale is high-risk and out of scope (Non-Goal). A shared chip subcomponent gives both popovers identical chip UX with one implementation.
- *Alternative considered:* migrate `IssueModelSelector` default model fully onto shared `ModelSelect` and port recent/loading/fuzzysort into it. Rejected for this issue: scope/risk too high for a P3 ergonomic change; tracked as a possible follow-up.

### D3: Variant helpers live in a small `model-variants.ts` util next to `ModelSelect`

Create `packages/web/src/shared/ui/model-variants.ts` exporting:
- `variantListFor(modelId, modelVariants): string[]` — lookup with safe default `[]`.
- `resolveVariantAgainstModel(modelId, variant, modelVariants): string | null` — returns the variant if the model still exposes it, else `null` (handles a selected variant whose model changed or lost the variant).

**Rationale:** the issue names these helpers as things to "relocate from `VariantPicker`", but `VariantPicker` doesn't exist, so they are created fresh here. Co-locating with `ModelSelect` keeps the shared UI self-contained. `VariantPicker.tsx` will not be created at all — it is removed-from-the-roadmap rather than removed-from-code.

### D4: Keyboard navigation via an explicit focus-zone state machine

Current state: a single `highlightedIndex` over `filtered[]`, handled in `handleKeyDown` on the search `<Input>`. Extend with:

- `chipFocus: { modelIndex: number; chipIndex: number } | null` — `null` means focus is on the model list (current behavior).
- Transitions:
  - `ArrowDown`/`ArrowUp`: when `chipFocus` is null, move `highlightedIndex` (unchanged). When `chipFocus` is set, move `chipIndex` within the row; at row bounds, move to the adjacent model row's first/last chip if it has variants, else drop back to model-list focus.
  - `ArrowRight`: when `chipFocus` is null and the highlighted model has variants, set `chipFocus = { highlightedIndex, 0 }`.
  - `ArrowLeft`: when `chipFocus` is set, clear it (back to model list) or move to previous chip.
  - `Enter`: if `chipFocus` set → select model + that chip's variant; else select highlighted model (default variant).
  - `Tab`: moves into the chip row of the highlighted model if it has variants (mirrors `ArrowRight`).
  - `Escape`: close popover, unchanged.

Imperative `.focus()` on chip `<button>` elements via refs is used so screen readers and visual focus rings follow `chipFocus`; the search `<Input>` remains the roving entry point.

**Rationale / alternatives:**
- *Alternative considered:* rely purely on native Tab order through focusable chips. Rejected: the issue specifies `→` to enter the chip row, which native Tab doesn't provide, and uncontrolled Tab would exit the popover unpredictably.
- *Alternative considered:* a full roving-tabindex library. Rejected: overkill for 2-4 chips per row; the state machine is small and testable.

### D5: Data plumbing — a `useModelVariants()` hook, optional and resilient

Add a settings-entity hook (e.g. in `entities/settings`) returning `Record<string, string[]>` sourced from runner model discovery once #238 exposes it. Until then it returns `{}` and the prop is omitted, so no chips render and all current behavior is preserved. `IssueModelSelector` and `AiSettingsSection` pass `modelVariants={data}` and `valueVariant={...}` through to `ModelSelect`.

The selected variant for the issue default / per-stage is read from the issue's `modelVariant` / `stageModelVariants` fields (the issue already carries these — see `stageModelVariants: { plan: "max", check: "high" }` in the issue payload) and written back through the same workflow-variable patch endpoints already used for models (`patchIssueWorkflowDefinitionVar` / `patchIssueWorkflowStageDefinitionVar`).

## Risks / Trade-offs

- **[Bespoke-vs-shared divergence]** `IssueModelSelector`'s default-model popover and the shared `ModelSelect` will now both render chips via the shared subcomponent, but their surrounding list/search/recent behavior stays different. -> *Mitigation:* chip subcomponent is purely presentational and self-contained; divergence is confined to non-chip row chrome. Document as a follow-up to unify if desired.
- **[Variant data absent at merge]** If #238 lands after this issue, the UI ships with no chips visible. -> *Mitigation:* design is fully additive and no-op without data; discoverability benefit is deferred but the refactor (single control, removed VariantPicker plans) still lands cleanly. Acceptance criteria that assert chip rendering are gated on variant data being supplied in tests.
- **[Keyboard complexity]** The `chipFocus` state machine adds branches to `handleKeyDown`. -> *Mitigation:* cover every transition with a unit test (the issue requires keyboard-nav tests); keep the machine in one function.
- **[Mobile tap-target regression]** Shrinking chips for `compact` could drop below 44px. -> *Mitigation:* chip padding is sized so the hit area meets 44px even when the visible chip is smaller; add a mobile-viewport render test.
- **[Breaking `onChange` callers]** None expected — `onChange` signature is unchanged. -> *Mitigation:* type-only addition; existing test suite is the regression guard.

## Migration Plan

1. **Add the foundation (no caller changes):** `model-variants.ts` helpers; extend `ModelSelectProps` with optional `modelVariants`/`valueVariant`/`onChangeVariant`; add `ModelVariantChips` subcomponent; wire chip rendering + `chipFocus` keyboard machine into `ModelSelect`. All existing tests must still pass with no variant props supplied.
2. **Tests:** extend `ModelSelect.test.tsx` with chip rendering, chip-click selection, keyboard nav into chips, compact sizing, mobile wrap. (No `VariantPicker.test.tsx` to update — it doesn't exist; do not create it.)
3. **Wire data:** add `useModelVariants()` (returns `{}` until #238).
4. **Migrate `AiSettingsSection`:** pass `modelVariants` + `valueVariant`/`onChangeVariant` to both default and per-stage `ModelSelect` instances; persist variant via the stage-model mutation path.
5. **Migrate `IssueModelSelector`:** render `ModelVariantChips` inside its bespoke `ModelListItem` (default model) and pass variant props to the shared per-stage `ModelSelect`; persist variant through the workflow-variable patch endpoints alongside model.
6. **Migrate `CreateIssueDialog`:** pass variant props to its `ModelSelect`; include variant in the create request when set.

**Rollback:** every change is additive behind optional props. Reverting the caller wiring (steps 4-6) restores today's exact UX with no schema or API migration. The shared-component additions (steps 1-2) are inert without variant data.

## Open Questions

- **Q1:** Does `CreateIssueDialog` actually use the shared `ModelSelect`, or a bespoke control? (Grep shows it uses `ModelSelect`-adjacent buttons; confirm during implementation — if bespoke, apply the D2 shared-subcomponent approach there too.)
- **Q2:** Where exactly does variant data arrive from #238 — a new API field on the model-discovery response, or a separate endpoint? Shapes the `useModelVariants()` implementation but not this design (the hook abstracts it).
- **Q3:** Should selecting a variant on a *different* model row auto-clear when the user clicks a new model's body? D1 assumes yes (body click ⇒ `onChangeVariant(null)`). Confirm with the issue's "default variant" acceptance criterion — it reads as yes.
- **Q4:** Agent editor page — does it share `ModelSelect` or have its own picker? Out of scope here but worth confirming it inherits correctly (per issue's out-of-scope note).
