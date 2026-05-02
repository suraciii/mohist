## Context

`AiSettingsSection.tsx` is a 638-line component in the web UI that manages AI provider configuration and model selection. It has two problems:

1. **Broken Popover**: The `ModelSelect` sub-component (line 173–327) wraps `Popover.Panel` in a `Transition` from `@headlessui/react` v2. In v2, `Transition` does not auto-detect `Popover`'s `open` state — it needs an explicit `show` prop. Without it, `Transition` defaults to closed and never renders the panel DOM.

2. **Poor provider list UX**: The `AiSettingsSection` render (line 446–637) outputs providers → custom providers → model selection → stage overrides in that order. With 80+ unconfigured providers, the model selection section is pushed far below the fold.

The project already has `@headlessui/react` v2.2.10 installed. No dependency changes needed.

## Goals / Non-Goals

**Goals:**
- Fix all `ModelSelect` popover selectors so they open and function correctly
- Reorder the page layout so Model Selection appears before Provider list
- Group providers into configured/unconfigured sections with collapsible unconfigured section

**Non-Goals:**
- Redesigning the overall settings page navigation (handled by `SettingsPage.tsx`, unchanged)
- Adding new API endpoints or backend changes
- Changing the `ProviderConnectDialog` or `CustomProviderDialog` components
- Adding pagination or virtualization for the provider list (search + collapse is sufficient)

## Decisions

### D1: Remove `Transition` wrapper entirely

Remove the `Transition` wrapper (line 257–265, 322) from `ModelSelect` and render `Popover.Panel` directly as a child of `Popover`. This is the Headless UI v2 idiomatic approach — `Popover.Panel` handles its own enter/leave animation via the `transition` prop.

**Alternatives considered:**
- Adding `show={open}` to `Transition` — works but keeps unnecessary wrapper code; `Popover.Panel` in v2 already supports `transition` prop natively
- Using `Popover.Panel transition` prop — equally valid; the simplest path is removing `Transition` entirely

### D2: Reorder sections — Model Selection first

Move the "Model Selection" section (currently line 532–563) and "Stage Model Overrides" section (line 568–597) above the "Providers" section (line 449–497). New order:

1. Model Selection (Mohist Model + Coder Model)
2. Stage Model Overrides (collapsible)
3. Providers (configured → unconfigured collapsible)
4. Custom Providers

**Alternatives considered:**
- Tabs for models vs providers — over-engineering for a settings page
- Keeping current order with anchor links — doesn't solve the scroll problem

### D3: Collapsible "Available Providers" section

Add a `unconfiguredExpanded` state (default `false`). The unconfigured providers render inside a collapsible container with a clickable header showing the count (e.g., "Available Providers (78)"). Configured providers always render expanded. The existing `providerSearch` state and `filteredProviders` memo continue to filter across both groups.

Implementation: simple `useState(false)` toggle + conditional rendering. No need for Headless UI `Disclosure` — the interaction is trivial and avoids introducing another Headless UI component with version concerns.

**Alternatives considered:**
- Headless UI `Disclosure` component — adds dependency complexity given the current v1/v2 migration situation
- Virtualized list — overkill; search + collapse solves the density problem

### D4: Remove `Transition` import

After removing the `Transition` wrapper, clean up the import on line 2: remove `Transition` from `@headlessui/react` import. Also remove `Fragment` import from React (line 1) if only used by the `Transition as={Fragment}`.

## Risks / Trade-offs

- [Popover loses enter/leave animation] → Acceptable; Panel appears/disappears instantly. Can add CSS `transition` classes to `Popover.Panel` later if needed — `Popover.Panel` in v2 supports a `transition` boolean prop.
- [Users accustomed to scrolling to model selection] → Low risk; new placement is more intuitive and matches typical settings UX patterns.

## Migration Plan

Single deploy — no backend changes, no data migration. The fix is purely frontend. Rollback is a simple git revert.

## Open Questions

None.
