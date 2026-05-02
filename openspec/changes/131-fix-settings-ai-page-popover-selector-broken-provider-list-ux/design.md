## Context

`AiSettingsSection.tsx` (638 lines) is the sole affected component. It uses `@headlessui/react` v2.2.10 but its `ModelSelect` sub-component applies a v1 pattern: wrapping `Popover.Panel` in `Transition` without `show={open}`. In v2, `Transition` no longer auto-detects `Popover`'s open state, so the panel never renders.

The page layout currently renders sections in this order: Providers (flat list of 80+ items) → Custom Providers → Model Selection → Stage Overrides. This buries the most-used controls at the bottom.

## Goals / Non-Goals

**Goals:**
- Make all model selection popovers functional (Mohist Model, Coder Model, Stage Overrides)
- Reorder sections so Model Selection is at the top
- Split provider list into Connected / Available groups with Available collapsed by default

**Non-Goals:**
- Redesigning the provider card UI (ConnectedProviderCard, AvailableProviderCard, CustomProviderCard stay as-is)
- Changing the provider search/filter behavior
- Adding provider categorization (e.g., by vendor, by region)
- Touching `SettingsPage.tsx` layout or routing

## Decisions

### D1: Remove Transition wrapper entirely

Remove the `<Transition as={Fragment}>` wrapper from `Popover.Panel` (lines 257–265, 322). Use `Popover.Panel` directly — Headless UI v2's `Popover.Panel` already handles open/close animation via CSS `transition` classes on the panel element itself if desired.

**Alternatives considered:**
- Add `show={open}` to Transition — works but keeps unnecessary indirection; the render prop `{({ open }) => ...}` already proves v2's `Popover` exposes open state correctly. The Transition layer adds nothing here.

### D2: Collapsible Available section via local state

Add a boolean state `availableOpen` (default `false`). Render the Available group header as a clickable button with a chevron icon and count text (e.g., "12 available providers"). Clicking toggles `availableOpen`. When collapsed, only the header renders. This mirrors the existing `stageOverridesOpen` pattern already in the same file.

**Alternatives considered:**
- Headless UI `Disclosure` component — overkill for a single collapsible; adds another import for no benefit.
- Virtualized list — premature optimization; 80 items render fine once collapsed by default.

### D3: Reorder JSX sections in AiSettingsSection

Move the "Model Selection" `<div>` block (currently lines 532–564) to be the first section. New order:
1. Model Selection (Mohist Model + Coder Model)
2. Stage Model Overrides (already collapsible)
3. Connected Providers (new group header)
4. Available Providers (collapsed by default)
5. Custom Providers

This places the most interactive elements at the top and the potentially long provider list at the bottom in a collapsed state.

**Alternatives considered:**
- Split into sub-components / separate pages — over-engineering for this fix scope.

## Risks / Trade-offs

- [Popover.Panel loses enter/exit animation] → Acceptable; the panel appears/disappears instantly. Can add CSS `transition` on `Popover.Panel` later if needed — v2 supports `transition` prop directly on `Popover.Panel`.
- [Available section default-collapsed means new users may not discover providers] → Mitigated by showing count in the collapsed header (e.g., "12 available providers"), and the search box inside the collapsed section is still accessible once expanded.

## Migration Plan

No migration needed — this is a UI-only fix with no API or data changes. Deploy via normal web build.

## Open Questions

None.
