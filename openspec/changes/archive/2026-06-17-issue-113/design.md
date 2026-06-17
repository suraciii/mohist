## Context

`ModelSelect.tsx` uses `@base-ui/react/popover` (a Base UI Popover wrapping `@base-ui/react/button` Base UI Buttons) for the dropdown. When a user clicks a model option with a real mouse, Base UI Popover's dismissable layer fires its native `pointerdown` listener at the document level during the **bubble phase**, which closes the popover and removes the button from the DOM **before** the browser dispatches the `click` event to the now-removed element. React synthetic events (`onClick`, `onPointerDown`) are batched and processed after native events complete, so none of them reach the handler either.

Validation evidence: programmatic `el.click()` and `dispatchEvent(new MouseEvent('click', ...))` both work because they fire synchronously outside the normal event pipeline. Keyboard Enter works because it goes through the search input's `onKeyDown`, not the button's `onClick`.

This bug affects all 5 model selectors on the Settings → Coder Agent page and any other `ModelSelect` usage.

## Goals / Non-Goals

**Goals:**
- Real mouse clicks on popover model options trigger `onChange` and close the popover
- Keyboard navigation (Arrow keys, Enter) remains functional
- Search filtering remains functional
- X clear button remains functional
- Existing tests pass; new tests cover the mouse-click selection path

**Non-Goals:**
- Replacing `@base-ui/react/popover` with `@radix-ui/react-popover` (out of scope)
- Visual redesign or component refactoring of `ModelSelect`
- Unified styling between Base UI Button and shadcn buttons (separate tech debt issue)

## Decisions

### Decision 1: Use native `pointerdown` event listener with event delegation

**Choice**: Attach a single native `pointerdown` listener on the popover's scrollable list container via `useRef` + `useEffect`. Use event delegation (`closest('[data-model-id]')`) to detect clicks on model option buttons.

**Rationale**:
- A native `pointerdown` listener fires during bubble phase **at the container element**, before the event reaches the **document** where Base UI's dismiss handler lives
- React synthetic events (`onClick`, `onPointerDown`) are batched by React's event system and processed **after** native events resolve — by then the popover content is already removed from DOM
- `e.stopPropagation()` in the native listener prevents Base UI's dismiss from firing, eliminating the redundant close
- Event delegation avoids attaching/detaching N listeners for each rendered option

**Alternatives considered**:

| Alternative | Verdict |
|---|---|
| `onClick` with `e.stopPropagation()` (Option C) | Won't work — `click` fires after `pointerdown`; the popover is already dismissed |
| `onPointerDown` (React synthetic, Option A) | Won't work — React batches synthetic events; the native dismiss fires first |
| `onPointerDownCapture` (React synthetic) | Won't work — same React batching issue |
| `onPointerDownOutside` on PopoverContent (Option B) | Not applicable — the problem is clicks **inside** the popover, not outside |
| Replace with `@radix-ui/react-popover` (Option D) | Larger scope, new dependency, out of scope per issue |

**Implementation sketch**:

```tsx
const listRef = useRef<HTMLDivElement>(null)

useEffect(() => {
  const el = listRef.current
  if (!el) return
  const handlePointerDown = (e: PointerEvent) => {
    const modelId = (e.target as HTMLElement)
      .closest('[data-model-id]')
      ?.getAttribute('data-model-id')
    if (modelId) {
      e.stopPropagation()
      selectModel(modelId)
    }
  }
  el.addEventListener('pointerdown', handlePointerDown)
  return () => el.removeEventListener('pointerdown', handlePointerDown)
}, [selectModel])
```

Each option button gets `data-model-id={model.id}` and retains its existing `onClick` handler (for keyboard-triggered selection and as a fallback).

### Decision 2: Keep existing `onClick` handler on buttons

The button's `onClick={() => selectModel(model.id)}` is retained. It provides a fallback for:
- Keyboard Enter (though this path goes through the input's `onKeyDown`)
- Programmatic clicks (which bypass the native dismiss issue)
- Future-proofing if Base UI fixes the upstream issue

The native `pointerdown` handler runs first and calls `selectModel`, which sets `open = false`. The subsequent `onClick` would try to call `selectModel` again, but since `open` is already `false` (unchanged), the `setOpen(false)` call is a React no-op. The `onChange` double-fire is harmless since it's the same `modelId`.

## Risks / Trade-offs

- **[Low] `stopPropagation` may suppress Base UI Button internal behavior** — The Base UI Button's `pointerdown` handling (focus management, active state) happens at the element level; `stopPropagation` prevents the event from reaching document-level listeners, not the element itself. Mitigation: verify visually that button active/pressed styling still works.
- **[Low] Double `onChange` fire** — The native `pointerdown` fires `selectModel(modelId)`, then `onClick` fires `selectModel(modelId)` again. Both calls are idempotent (same argument, `setOpen(false)` is no-op on second call). No observable side effect.
- **[Low] Future Base UI update may change event internals** — If Base UI changes its dismiss mechanism (e.g., to use `click` instead of `pointerdown`), this fix may become unnecessary. Mitigation: the fix is additive and safe; the native listener doesn't conflict.

## Migration Plan

1. **Deploy**: Standard web package deploy. No database changes, no API changes, no dependency changes.
2. **Rollback**: Revert the `ModelSelect.tsx` change. No migration needed.
3. **Verification**: Test on Settings → Coder Agent page by clicking model options with a real mouse, confirming PATCH request fires and toast appears.

## Open Questions

None. Root cause and fix are both well-understood.
