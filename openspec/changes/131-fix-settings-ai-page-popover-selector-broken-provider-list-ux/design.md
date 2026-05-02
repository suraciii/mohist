## Context

`AiSettingsSection.tsx` is a single 638-line file containing all AI settings UI: provider management, model selection popovers, and stage overrides. The immediate blocker is that `ModelSelect` (line 229–327) wraps `Popover.Panel` in a Headless UI v1 `Transition` (line 257–265), but `@headlessui/react` v2.x dropped implicit open-state detection on `Transition`. Without `show={open}`, the panel never renders. The secondary problem is layout: providers (80+ cards) appear first, pushing Model Selection to the bottom.

## Goals / Non-Goals

**Goals:**
- Unbreak all `ModelSelect` instances (Mohist Model, Coder Model, 5 stage overrides)
- Reorder sections: Model Selection → Providers → Custom Providers → Stage Overrides
- Collapse unconfigured providers into a default-collapsed "Available Providers" group

**Non-Goals:**
- No new components or files — all changes stay in `AiSettingsSection.tsx`
- No Headless UI version change — use v2 API as-is
- No provider filtering by category/region — just connected vs unconfigured split

## Decisions

### D1: Remove `Transition` wrapper entirely (not `show={open}` patch)

Strip lines 257–265 (`<Transition as={Fragment} ...>`) and the closing `</Transition>` at line 322. `Popover.Panel` in v2 handles its own mount/unmount. Loss of enter/leave CSS transitions is acceptable — the panel appears/disappears instantly, which is standard for dropdown menus.

**Alternatives considered:**
- `show={open}` on Transition — works but keeps unnecessary abstraction for a simple dropdown; more code, same result
- CSS-only transition via `data-*` attributes (v2 `transition` prop) — v2 `Popover.Panel` supports a `transition` prop natively, but adds complexity for marginal gain on a utility dropdown

### D2: Section reorder via JSX block swap

Move the "Model Selection" `<div>` block (lines 532–564) to appear as the first section inside the `<div className="space-y-8">` container. Then Providers, Custom Providers, Stage Overrides follow.

### D3: Collapsible unconfigured providers via local state

Add `const [availableExpanded, setAvailableExpanded] = useState(false)`. In the Providers section, render configured providers always visible, then render a clickable header like "Available Providers (75)" that toggles the unconfigured list. Reuse `ChevronRightIcon` + `rotate-90` pattern already used for Stage Overrides.

The search bar stays above both groups and filters across them. When search is active, both groups expand to show matching results regardless of `availableExpanded`.

## Risks / Trade-offs

- [No transition animation on open/close] → Acceptable trade-off; model selector is a utility, not a showcase component
- [Search with collapsed group needs special handling] → When `providerSearch` is non-empty, force-expand the available group to show filtered results

## Migration Plan

Single PR, no data migration. Deploy with build verification.
