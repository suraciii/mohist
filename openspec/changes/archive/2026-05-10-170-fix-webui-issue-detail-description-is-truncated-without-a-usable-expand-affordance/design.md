## Context

The Issue Detail page renders issue descriptions with a 600px height limit and a gradient fade-out overlay. The Expand/Collapse button is currently nested inside the same `overflow-hidden` container, causing it to be obscured by the gradient or clipped when the description is long. Users cannot reliably discover or click the control.

Additionally, the button is rendered unconditionally for any non-empty description, including very short ones where expansion is unnecessary.

## Goals / Non-Goals

**Goals:**
- Make the Expand/Collapse control always visible and reachable, regardless of description length.
- Only show the control when the rendered Markdown content actually exceeds the collapse threshold (~600px).
- Preserve the existing collapse/expand state toggle behavior.
- Add regression tests for the conditional visibility and interaction.

**Non-Goals:**
- Changing the 600px threshold value.
- Adding animation/transition effects for expand/collapse.
- Modifying Markdown rendering or styling.
- Changing the behavior of other sections (comments, changes panel, etc.).

## Decisions

### D1: Move the control outside the overflow-clipped container

The Expand/Collapse button will be moved from inside the `relative overflow-hidden` wrapper to a sibling element below it. This guarantees the button is never clipped or covered by the gradient overlay.

**Alternatives considered:**
- Increase gradient overlay z-index and position button absolutely at the bottom — still risks partial overlap and reduced clickable area.
- Remove the gradient entirely — would make truncated content look abruptly cut off, degrading visual polish.

### D2: Use DOM measurement to conditionally render the control

A `ref` on the inner Markdown container plus `useEffect` (or `useLayoutEffect`) will measure `scrollHeight` after render. If `scrollHeight > 600px`, the control is rendered; otherwise it is omitted.

**Alternatives considered:**
- Estimate height from character count — unreliable because Markdown rendering (headings, lists, code blocks) significantly affects line height.
- Always render the control unconditionally — fails the "no meaningless button" acceptance criterion.

### D3: Keep the gradient overlay only in collapsed state

The gradient fade (`bg-gradient-to-t from-white to-transparent`) remains visible only when collapsed and content overflows. When expanded, both the gradient and height constraint are removed.

**Alternatives considered:**
- Keep gradient always — would overlay expanded content, making the bottom unreadable.

## Risks / Trade-offs

- [Risk] DOM measurement triggers an extra layout/reflow on every description render → Mitigation: measure only once after initial render (empty dependency array or body content change), not on every state update.
- [Risk] Test environment (jsdom) may not accurately compute `scrollHeight` → Mitigation: mock the ref or use `getBoundingClientRect`/`scrollHeight` stubs in tests, or test conditional rendering via prop-driven assertions where feasible.

## Migration Plan

1. Refactor `IssueDetailPage.tsx` description section layout:
   - Wrap `MarkdownContent` in a measured container with a ref.
   - Move the Expand/Collapse button outside the overflow-clipped wrapper.
   - Add `useEffect` to set an `isOverflowing` flag based on `scrollHeight > 600`.
   - Conditionally render the button only when `isOverflowing` is true.
2. Update existing tests in `IssueDetailPage.test.tsx`:
   - Remove or adjust the test that asserts "Expand" appears for short descriptions.
   - Add a test verifying the button is absent when content fits within 600px.
   - Retain existing expand/collapse interaction tests.
3. Run test suite: `cd packages/cli/web && npm test`.
4. Manual verification: open a long issue (e.g., #144) and confirm the Expand button is clearly visible below the gradient; verify short issues show no button.

## Open Questions

- None.

