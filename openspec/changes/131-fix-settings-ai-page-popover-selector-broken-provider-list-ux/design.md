## Context

The AI Settings page (`AiSettingsSection.tsx`) has two problems:

1. **Critical bug**: `ModelSelect` wraps `Popover.Panel` in a `<Transition>` (lines 257-265) without `show={open}`. The project uses `@headlessui/react` v2.2.10, where `Transition` no longer auto-detects Popover's open state. Without `show`, Transition defaults to closed, preventing the panel from ever rendering.

2. **Poor UX**: The page renders sections in this order: Providers (80+ flat list) → Custom Providers → Model Selection → Stage Overrides. Model Selection — the most actionable section — is buried at the bottom.

Current page order (line 448-597):
```
Providers (flat, all 80+ mixed together)
  → Custom Providers
    → Model Selection (Mohist + Coder)
      → Stage Model Overrides
```

## Goals / Non-Goals

**Goals:**
- Make all model selectors functional (Popover.Panel renders on click)
- Reorder page so Model Selection is the first section
- Split Providers into Connected (always visible) and Available (collapsed by default)

**Non-Goals:**
- Redesigning the provider card components (ConnectedProviderCard, AvailableProviderCard)
- Changing the ProviderConnectDialog or CustomProviderDialog
- Adding provider categorization (e.g., by region/type)
- Modifying the Headless UI version or adding new dependencies

## Decisions

### D1: Remove Transition wrapper entirely

Remove the `<Transition>` wrapper around `<Popover.Panel>` in ModelSelect. Use `<Popover.Panel>` directly with its built-in `transition` prop for enter/leave animations (Headless UI v2 approach).

This is the minimal fix. `Popover.Panel` in v2 supports a `transition` boolean prop and animation class names directly, eliminating the need for a separate `Transition` component.

**Alternatives considered:**
- Passing `show={open}` to Transition: works but keeps unnecessary wrapper complexity
- Using `<Transition show={open}>`: functional but v2 idiomatic pattern is to use Panel's built-in transition support

### D2: Reorder sections to Model Selection first

New page order:
```
Model Selection (Mohist + Coder)
  → Stage Model Overrides
    → Connected Providers (always expanded)
      → Available Providers (collapsed by default)
        → Custom Providers
```

Users interact with model selection most frequently. Connected providers are secondary reference. Available providers are rarely needed (one-time setup).

### D3: Available Providers section collapsed by default

Add an `availableProvidersOpen` state (default `false`). Render connected providers inline. Render available providers inside a collapsible section with a summary header like "Available Providers (N)" that expands on click.

Remove the existing provider search input — with connected providers separated and available providers collapsed, the list is manageable without search.

**Alternatives considered:**
- Keep search but also collapse: unnecessary UI complexity for a rarely-used list
- Paginate available providers: over-engineering for a settings page

## Risks / Trade-offs

- [Lost transition animation briefly during implementation] → Use `Popover.Panel`'s `transition` prop to preserve animation without `Transition` wrapper
- [Removing provider search may frustrate users looking for a specific provider] → The collapsed section still has a clear expand action; provider names are visible on expand

## Migration Plan

Single PR, no backend changes. Deploy with next build.

## Open Questions
