## Context

`AiSettingsSection.tsx` has two problems: (1) the `ModelSelect` component wraps `Popover.Panel` in a `Transition` without `show={open}`, which in Headless UI v2 keeps the panel permanently hidden; (2) the page layout renders Providers first (80+ flat items), burying Model Selection at the bottom. The existing `Transition` import from `@headlessui/react` and the `Fragment` import from React are only used by this broken wrapper.

## Goals / Non-Goals

**Goals:**
- Fix all ModelSelect popovers so they open and close correctly under Headless UI v2
- Reorder the AI settings page: Model Selection first, then Providers
- Collapse unconfigured providers behind a toggle to reduce visual noise

**Non-Goals:**
- Redesigning the provider connection flow or dialog
- Changing any API endpoints or data structures
- Adding animation/transition effects to the popover (the current Transition was broken, and Popover.Panel in v2 has built-in enter/leave handling via `transition` prop if desired later)

## Decisions

### D1: Remove `Transition` wrapper entirely

Replace lines 257–265 (`<Transition as={Fragment} ...>`) and its closing tag (line 322) with nothing — `Popover.Panel` renders directly as a child of the render-prop fragment.

In Headless UI v2, `Popover.Panel` manages its own open/close state via the `Popover` context. The `Transition` component in v2 does not auto-detect this context; it requires an explicit `show` prop. Passing `show={open}` would also work, but removing `Transition` entirely is simpler, matches the v2 recommended pattern, and eliminates the unnecessary `Fragment` wrapper.

After this change the `Transition` and `Fragment` imports become unused and should be removed.

**Alternatives considered:**
- Pass `show={open}` to `Transition` — works but adds an unnecessary wrapper for no benefit in v2
- Use `Popover.Panel`'s built-in `transition` prop (v2 feature) — possible future enhancement, but out of scope

### D2: Reorder page sections in `AiSettingsSection`

Change the JSX order inside the `AiSettingsSection` return to:
1. **Model Selection** (Mohist Model + Coder Model)
2. **Stage Model Overrides** (existing collapsible section, stays as-is)
3. **Providers** — split into two subsections:
   - Connected providers (full list, always visible)
   - Unconfigured providers (collapsed by default)
4. **Custom Providers** (existing section, stays as-is)

This places the most-used controls first without changing any logic — purely a JSX reorder within the same component.

### D3: Unconfigured providers collapsed by default

Add a `showUnconfigured` state (boolean, default `false`). When collapsed, render a single clickable row showing "N providers available — click to expand". When expanded, render the existing `AvailableProviderCard` list plus a search input.

Use the existing `unconfiguredProviders` memo — no new data fetching needed.

**Alternatives considered:**
- Two-column layout (connected left, unconfigured right) — too cramped on mobile
- Pagination — overkill for a settings page

## Risks / Trade-offs

- [Lost enter/leave animation] → Popover appears/disappears instantly instead of fading. Acceptable for a bug fix; can add `Popover.Panel transition` prop later if desired.
- [Unconfigured providers hidden by default] → Users may not discover all providers. Mitigated by showing count and a clear "expand" affordance.

## Migration Plan

Single deploy — this is a frontend-only bug fix + UX improvement with no API or data changes. No rollback needed beyond a git revert.

## Open Questions

None.
