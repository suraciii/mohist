## Context

The AI Settings page (`AiSettingsSection.tsx`) has two problems:

1. **Popover bug**: `ModelSelect` wraps `Popover.Panel` in a bare `<Transition>` (lines 257–265). In `@headlessui/react` v2, `Transition` no longer auto-detects `Popover`'s internal `open` state. Without `show={open}`, it stays in closed state permanently — the Panel never renders to DOM.

2. **Layout**: The page renders sections in order: Providers (flat 80+ items) → Custom Providers → Model Selection → Stage Overrides. The most-used controls (model selection) are buried at the bottom behind a wall of unconfigured providers.

Current page section order in `AiSettingsSection` JSX (lines 448–597):
- Providers (search + flat list of configured + unconfigured)
- Custom Providers
- Model Selection (Mohist + Coder)
- Stage Model Overrides (collapsible)

Existing data already computed: `configuredProviders`, `unconfiguredProviders`, `customProviders` (lines 354–356).

## Goals / Non-Goals

**Goals:**
- Fix all ModelSelect popovers so they open and function correctly with Headless UI v2
- Promote Model Selection to top of page
- Split the flat provider list into Connected (expanded) and Available (collapsed) sections

**Non-Goals:**
- No changes to backend APIs, data models, or provider connect/disconnect logic
- No new dependencies — use existing `@headlessui/react` v2
- No changes to `ProviderConnectDialog`, `CustomProviderDialog`, or `SettingsPage.tsx`
- No transition animations on Popover (removing Transition is the fix; re-adding animation is a separate concern)

## Decisions

### D1: Remove Transition wrapper entirely

Remove the `<Transition>` wrapper from `Popover.Panel` (lines 257–265). In Headless UI v2, `Popover.Panel` handles its own show/hide via the parent `Popover` context. The v1 pattern of wrapping in `Transition` without `show` is the root cause.

**Alternatives considered:**
- **Add `show={open}` to Transition**: Would work but adds unnecessary complexity. The `Popover.Panel` in v2 already handles open/close transitions internally. Extra wrapper is dead code.
- **Use `Popover.Panel`'s built-in `transition` prop**: Headless UI v2 supports `transition` and animation class props directly on `Popover.Panel`. This is the cleanest v2-native approach, but adds no functional value beyond what bare `Popover.Panel` provides — skip for now to keep the fix minimal.

### D2: Reorder sections: Model Selection first, Providers second

Move the Model Selection JSX block (lines 532–563) and Stage Model Overrides block (lines 568–597) above the Providers section. New order:

1. Model Selection (Mohist + Coder)
2. Stage Model Overrides (collapsible)
3. Connected Providers (expanded)
4. Available Providers (collapsed, with count badge)
5. Custom Providers

**Alternatives considered:**
- **Two-column layout**: Would require responsive handling and doesn't solve the "80 items in a list" problem. Over-engineered for a settings page.
- **Tabs for providers vs models**: Adds navigation complexity; users want to see model selection and connected providers at a glance.

### D3: Available Providers section default-collapsed with count

Add a `useState` for Available Providers collapse state. Render the section header as a clickable toggle showing "Available Providers (N)" with a `ChevronRightIcon`. Default to collapsed (`availableOpen = false`). The search input moves into the Connected Providers section header area, or is removed (available providers are less frequently accessed).

**Alternatives considered:**
- **Virtual scrolling**: Over-engineered for a settings page; collapsing solves the information density problem.
- **Category tabs within providers**: Would require grouping logic by provider type/region. Not enough benefit for the complexity.

## Risks / Trade-offs

- [Losing open/close animation on Popover] → Acceptable trade-off. The fix is removing the broken Transition. Animation can be re-added later using v2's `transition` prop on `Popover.Panel` directly.
- [Users may not discover Available Providers if collapsed] → Mitigated by showing the count badge ("Available (78)") which signals there's more content to expand.
- [Existing provider search only applies to the flat list] → After reordering, the search input should appear above the combined Connected+Available list. When Available is collapsed, search still filters both and auto-expands Available if matches are found there.

## Migration Plan

No migration needed. This is a frontend-only fix with no data or API changes. Deploy by rebuilding the web assets (`cd packages/cli && npm run build`).

## Open Questions

None — the fix is straightforward and well-scoped.
