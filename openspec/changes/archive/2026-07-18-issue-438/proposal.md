## Why

The shared model selector selects on `pointerdown` — a workaround introduced in #113 to dodge a Base UI Popover dismiss-timing bug that no longer exists in the current library version. That workaround is the root cause of broken touch/pen scrolling: the `preventDefault()` on `pointerdown` kills native scroll initiation, so any finger/stylus contact with an option immediately selects it and closes the menu. The same non-standard "press-to-select" semantics is reimplemented differently in the bespoke issue-detail popover, the list lacks scroll containment (scroll chains to the page behind), group headers scroll out of view, and the options carry `role="button"` instead of listbox semantics. Now is the right time because the blocking library bug is gone, so the workaround can be removed rather than patched, and the component is already test-covered.

## What Changes

- **Selection moves from `pointerdown` to `click` (and keyboard Enter)** across both the shared `ModelSelect` and the bespoke `IssueModelSelector`. A press that moves off the option before release does not select; touch/pen gestures can scroll the list freely.
- **Native touch/pen scrolling restored.** Remove the `preventDefault()`/`stopPropagation()` on pointer events bound to selection in both implementations (the delegated native listener on the shared list container, and the inline `onPointerDown` on each bespoke `ModelListItem`).
- **Scroll containment.** The list scrolls independently with `overscroll-behavior: contain` so reaching the top/bottom edge does not chain to the page behind the popover.
- **Sticky provider group headers.** The current group header stays visible while its group is in scroll view, preserving context on long lists.
- **Visual affordances.** Options gain pointer cursor and hover/active states; the currently-selected model is clearly marked in the list; variant chip layout does not squeeze the option row.
- **Complete keyboard navigation, unified across both popovers.** ↑/↓ move highlight and scroll it into view, Enter selects, Esc closes (the bespoke popover's hint already promises Esc but it is not implemented), typing filters; variant chips stay reachable via →/Tab and selectable via Enter. Add Home/End to jump to first/last.
- **Standard combobox/listbox semantics.** Trigger + search input + option list expose proper roles, `aria-activedescendant`, `aria-selected`, and group labeling so assistive tech announces options as list items and reports selected state.
- **Unify the bespoke `IssueModelSelector` interaction layer with the shared `ModelSelect`** so all four surfaces share one click-to-select, keyboard-complete, accessible implementation. The divergence flagged as tech debt in #239 and #408 is resolved as part of the redo.
- **Migrate the interaction primitives onto the component library's current combobox/autocomplete primitive** (cmdk is already installed and wrapped; final choice in `design.md`), replacing the hand-rolled keyboard state machine, search filter, and scroll-into-view logic.

## Capabilities

- `model-select`: The model selector dropdown's interaction and accessibility contract — click-to-select (not press-to-select), native touch/pen scrolling with scroll containment, sticky provider group headers, visual affordances and selected-state indicator, complete keyboard navigation with scroll-into-view, standard combobox/listbox semantics, and consistent behavior across all four model-selection surfaces (Settings → AI default and per-stage, Issue config card default and per-stage, Create Issue dialog, Agent Profile editor). Covers the invariant "a press that does not become a click does not select" and "all four surfaces behave identically".

## Impact

- **Code**:
  - `packages/web/src/shared/ui/ModelSelect.tsx` — remove the native `pointerdown` delegation listener and the redundant dual `onClick`/`onPointerDown` paths; switch selection to click/Enter; add overscroll containment, sticky group headers, scroll-into-view on highlight, listbox roles + `aria-activedescendant` + `aria-selected`; complete keyboard nav (Home/End).
  - `packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx` — remove the bespoke `onPointerDown` selection on `ModelListItem`; unify its interaction layer with the shared component; implement the missing Esc handler.
  - `packages/web/src/shared/ui/components/command.tsx` / `popover.tsx` — likely consumption of the already-wrapped cmdk primitive to replace hand-rolled list/search/keyboard (final primitive choice in `design.md`).
  - Call sites unchanged at the prop-contract level: `pages/settings/ui/AiSettingsSection.tsx`, `pages/issue-detail/ui/cards/IssueConfigurationCard.tsx`, `features/create-issue/ui/CreateIssueDialog.tsx`, `widgets/agent-profile-editor/ui/AgentProfileEditor.tsx`.
- **Tests**:
  - `packages/web/src/shared/ui/ModelSelect.test.tsx` — flip `fireEvent.pointerDown(row)` selection assertions to `fireEvent.click(row)`; add cases for press-move-release no-select, scroll containment, sticky headers, Home/End, combobox roles.
  - `packages/web/src/features/select-issue-model/ui/IssueModelSelector.test.tsx` — same pointerdown→click flip; add Esc-closes case.
  - `packages/web/src/pages/settings/ui/AiSettingsSection.test.tsx` — keyboard-nav cases stay valid; adjust if chip focus handoff changes.
- **APIs / dependencies**: No backend API, data-contract, or model-listing changes. Likely broader adoption of the already-installed `cmdk`; no new third-party dependency.
- **Systems**: Web only. Runner, server, and CLI are unaffected.
