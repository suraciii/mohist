## Context

`AiSettingsSection.tsx` renders the AI Settings tab. The `ModelSelect` component (lines 173–327) uses `Popover` + `Transition` from `@headlessui/react` v2.2.10. The `Transition` wrapper at lines 257–265 omits `show={open}`, so in v2 the panel never mounts — all 7+ popover instances (Mohist Model, Coder Model, 5 Stage Overrides) are completely broken.

The provider list (`sortedProviders`) interleaves configured and unconfigured providers into a single flat list of 80+ cards. Model Selection is the last section before Stage Overrides, requiring a full scroll past the provider list.

## Goals / Non-Goals

**Goals:**
- Make all ModelSelect popovers functional under Headless UI v2
- Reorder sections: Model Selection → Connected Providers → Custom Providers → Available Providers (collapsible) → Stage Overrides
- Give unconfigured providers a collapsible section, defaulting to collapsed

**Non-Goals:**
- Adding new provider filtering/categorization beyond connected vs. unconfigured
- Changing the ProviderConnectDialog or CustomProviderDialog
- Modifying any API endpoints or data layer

## Decisions

### D1: Remove Transition wrapper entirely

Remove the `Transition` wrapper and render `Popover.Panel` directly. Headless UI v2's `Popover.Panel` handles its own enter/leave animation via CSS `data-*` attributes or can use the built-in `transition` prop. This is the simplest fix with zero behavioral regression — the current Transition does nothing (never opens).

**Alternatives considered:**
- Add `show={open}` to `Transition` — preserves animation but adds coupling; the `open` slot value from `Popover` render prop works but is fragile if Headless UI internals change
- Use Headless UI v2 `transition` prop on `Popover.Panel` — more idiomatic but unnecessary complexity for a dropdown that appears/disappears instantly

### D2: Reorder AiSettingsSection layout

Change the JSX section order in `AiSettingsSection`'s return to: Model Selection → Connected Providers (with search) → Custom Providers → Available Providers (collapsible) → Stage Overrides. This keeps all sections in one component; no extraction needed.

### D3: Collapsible unconfigured provider section

Use a local `useState` boolean (default `false`) to control an expand/collapse toggle on the unconfigured providers list. Reuse the existing `ChevronRightIcon` with rotation for the toggle indicator, matching the Stage Model Overrides pattern already in this file. Show a count badge (e.g., "+78 providers") when collapsed.

**Alternatives considered:**
- Headless UI `Disclosure` component — adds another dependency import for trivial show/hide
- Virtualized list for 80+ items — over-engineering; the list is hidden by default

## Risks / Trade-offs

- [Popover opens without enter animation] → Acceptable; the panel appears instantly which is standard for select dropdowns
- [Existing `Transition` import becomes unused] → Remove `Transition` from the import to avoid dead code
- [Section reorder may confuse existing users] → Mitigated by the fact that Model Selection is the most-used section and users will find it faster

## Migration Plan

Single PR with two logical commits: (1) fix Transition/Popover bug, (2) reorder sections + add collapsible unconfigured providers. No API changes, no data migration, no rollout needed — purely frontend.

## Open Questions

None.
