## Context

The AI Settings page (`AiSettingsSection.tsx`) has two problems:

1. **Broken popover**: `ModelSelect` wraps `Popover.Panel` in a `<Transition>` component. In `@headlessui/react` v2.x, `Transition` no longer auto-detects the `Popover`'s internal `open` state — without an explicit `show` prop it defaults to closed, so `Popover.Panel` never enters the DOM.

2. **Poor layout**: The page renders ~80 unconfigured providers as a flat list above Model Selection. Users must scroll past all of them to reach the controls they interact with most.

The component is self-contained in `AiSettingsSection.tsx` (638 lines). No backend or API changes are needed.

## Goals / Non-Goals

**Goals:**
- Make all ModelSelect popover instances functional (Mohist Model, Coder Model, Stage Overrides)
- Reorder the page so Model Selection is the first section
- Collapse unconfigured providers into an expandable section to reduce visual noise

**Non-Goals:**
- Redesigning the provider connection flow or dialog
- Adding provider categorization tags or favorites
- Changing any backend API or data model
- Upgrading or downgrading `@headlessui/react` version

## Decisions

### D1: Remove `<Transition>` wrapper entirely

Remove the `<Transition>` wrapper from around `<Popover.Panel>` (lines 257-265 and the closing tag at line 322). Use `Popover.Panel` directly with its built-in v2 transition support via the `transition` prop.

In Headless UI v2, `Popover.Panel` supports a `transition` prop and `enter`/`enterFrom`/`enterTo`/`leave`/`leaveFrom`/`leaveTo` classes directly — no separate `Transition` component needed.

**Alternatives considered:**
- Adding `show={open}` to the existing `<Transition>` — works but keeps an unnecessary wrapper; the v2 `Popover.Panel` already handles this natively
- Wrapping in `<Transition show={open}>` — adds a prop that Headless UI v2 Popover.Panel already manages internally

### D2: Reorder sections — Model Selection first

Reorder the JSX in `AiSettingsSection` return block to: Model Selection → Connected Providers → Available Providers (collapsed) → Custom Providers → Stage Model Overrides.

This is a pure JSX reorder, no state logic changes.

### D3: Collapsible "Available Providers" section

Split `filteredProviders` into `configuredProviders` (already computed) and `unconfiguredProviders` (already computed). Render configured providers in an always-open section. Render unconfigured providers inside a `<details>`/`<summary>` HTML element (or a simple `useState` toggle) with header text "Available Providers (N)" that defaults to collapsed.

Use a simple `useState<boolean>` toggle (matching the existing `stageOverridesOpen` pattern in the same component) rather than introducing a new dependency.

**Alternatives considered:**
- Native `<details>`/`<summary>` — works but can't easily animate; style control is limited
- Headless UI `Disclosure` — adds import for a one-off use; `useState` is simpler and consistent with the component's existing patterns

## Risks / Trade-offs

- [Popover loses enter/leave animation] → Acceptable; the panel appears/disappears instantly which is standard for dropdowns. The `transition` prop on `Popover.Panel` in v2 can restore animation without the `Transition` wrapper if desired later.
- [Reordering may confuse users accustomed to current layout] → Low risk; the new order matches the expected task flow (pick models first, then manage providers).

## Migration Plan

Single PR, no migration needed. Changes are purely frontend UI with no data model or API impact. No rollout strategy required.

## Open Questions

None.
