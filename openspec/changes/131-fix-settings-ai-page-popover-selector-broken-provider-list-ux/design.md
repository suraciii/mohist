## Context

`AiSettingsSection.tsx` renders the AI settings tab. The `ModelSelect` component (lines 173–327) wraps `Popover.Panel` in a `Transition` from `@headlessui/react`, which is the v1 API pattern. The project runs `@headlessui/react ^2.2.10`, where `Transition` no longer auto-detects `Popover`'s open state — without `show={open}`, the panel never renders. The provider list displays 80+ items in a flat sequence, pushing Model Selection off-screen.

## Goals / Non-Goals

**Goals:**
- Fix ModelSelect popover so panels render and are interactive
- Reorder AI settings page: Model Selection first, Providers second
- Group providers into "Connected" (expanded) and "Available" (collapsible, collapsed by default)

**Non-Goals:**
- Adding search/filter within the provider list (already exists)
- Changing the provider connect/disconnect API or dialog
- Adding animation/transition effects to the popover (removing Transition is intentional)
- Changing `SettingsPage.tsx` layout or routing

## Decisions

### D1: Remove Transition wrapper, use Popover.Panel directly

Strip the `<Transition>` block (lines 257–265) entirely. In Headless UI v2, `Popover.Panel` manages its own open/close visibility via the `Popover` context — no `Transition` needed. The panel will appear/disappear instantly without animation, which is acceptable for a dropdown selector.

**Alternatives considered:**
- Pass `show={open}` to `Transition` — works but keeps unnecessary wrapper complexity; the render function already provides `{ open }`, so this is a one-line fix, but it perpetuates a v1 pattern that could break again in future upgrades.
- Use a completely different library (e.g., Radix) — overkill for this scope; Headless UI v2's native `Popover` is sufficient.

### D2: Reorder sections — Model Selection before Providers

Move the "Model Selection" `<div>` block and "Stage Model Overrides" block above the "Providers" block in the JSX. This is a pure reorder with no logic changes. Users interact with model selection more frequently than provider setup.

### D3: Collapsible "Available Providers" section

Add a `useState<boolean>` for the available providers section. Render a clickable section header with a chevron icon. When collapsed, show only the header with a count badge (e.g., "Available (78)"). The "Connected" section remains always-expanded (no collapse toggle) since it's short.

**Alternatives considered:**
- Tab-based layout (Connected | Available tabs) — adds navigation complexity for little gain when connected providers are few.
- Virtualized list for 80+ items — over-engineering; collapsible section avoids the scroll problem entirely.

## Risks / Trade-offs

- [Loss of enter/exit animation] → Acceptable trade-off. The popover appears instantly, which is standard for dropdown selectors. Can re-add CSS transitions later if needed.
- [Available section hidden by default means new users won't see providers] → Mitigated: when zero providers are connected, the Available section defaults to expanded.

## Migration Plan

Single deploy — no API or data changes. Pure frontend component refactor. No rollback needed beyond reverting the commit.
