# Proposal: ModelSelect inline variant chips

## Why

The model selector is the most-touched control in the model-configuration flow, yet variant selection today is a disjoint two-step interaction: pick a model in one control, notice a separate variant dropdown appeared, then open it. The model list also gives no signal about which models expose reasoning variants, so users only discover variant support after selecting. With reasoning-variant delivery landing in #238, the UX needs to catch up so the capability is discoverable and selectable in a single action on every surface that picks a model.

## What Changes

- Extend `packages/web/src/shared/ui/ModelSelect.tsx` to accept variant data (`modelVariants: Record<string, string[]>`) and an optional selected-variant value, and render a row of small tappable chips inline on each model row that has variants.
- Clicking a model row's main body selects the model with the **default** (no) variant — current behavior preserved.
- Clicking a variant chip selects model + variant in one action and closes the popover.
- The currently-selected model row and the active variant chip within it are visually distinct from inactive chips.
- Add keyboard navigation: ↑↓ across model rows, → expands focus into the chip row of the highlighted model, Enter confirms either the model (default variant) or the focused chip, Tab moves into chips, Esc closes.
- Support a `size="compact"` chip rendering so per-stage override rows and compact surfaces scale correctly; chips remain ≥44px tap targets on mobile (375px), wrapping gracefully when a model has 4 variants.
- Migrate all three callers to the unified single `ModelSelect` (issue default + per-stage overrides in `IssueModelSelector`, project defaults in `AiSettingsSection`, issue creation in `CreateIssueDialog`).
- Remove the standalone `VariantPicker` for the cascading model case (if/where it exists); relocate its `variantListFor` / `resolveVariantAgainstModel` helpers into `ModelSelect` or a small adjacent utils module.

## Capabilities

### New Capabilities

- `model-select`: The unified shared `ModelSelect` component — a single Popover with a provider-grouped flat model list that renders inline variant chips on rows whose models expose variants, plus chip selection semantics, active-chip highlighting, keyboard navigation into chip rows, and compact/mobile sizing.

### Modified Capabilities

- `web-ui`: The "Web UI supports issue model overrides" requirement changes so issue default and per-stage model overrides are selected through the unified variant-aware `ModelSelect` (model + variant chosen in one control) rather than a model selector paired with a separate variant dropdown.

## Impact

- **Code**:
  - `packages/web/src/shared/ui/ModelSelect.tsx` — extended with variant props, inline chip subcomponent, keyboard model↔chip focus handoff.
  - `packages/web/src/shared/ui/ModelSelect.test.tsx` — new cases for chip rendering, chip-click selection, keyboard nav into chips, compact size.
  - `packages/web/src/shared/ui/VariantPicker.tsx` (+ test) — removed for the model case; helpers relocated.
  - `packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx` — drop paired variant control; pass variant data + selected variant into `ModelSelect`.
  - `packages/web/src/pages/settings/ui/AiSettingsSection.tsx` — project defaults selector migrated.
  - `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx` — issue-creation picker migrated.
- **Data**: Requires per-model variant data (`provider → model → variants[]`) to be reachable by the web layer. Sourced from runner model discovery; delivery wiring is tracked by #238 and treated as a prerequisite for end-to-end effect (the UI change is independently useful for discoverability beforehand).
- **APIs / dependencies**: No backend API contract changes in this issue; variant data is expected to flow through existing model-discovery data. No new third-party dependencies.
- **Systems**: Web UI only. Runner/server unaffected by this issue.
