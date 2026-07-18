## Context

The model selector is a shared Web control used on four surfaces: Settings → AI Settings (default model + per-stage overrides), the Issue detail configuration card (default + per-stage), the Create Issue dialog, and the Agent Profile editor. It exists today as **two parallel implementations** that diverge in interaction detail:

- `packages/web/src/shared/ui/ModelSelect.tsx` (470 lines) — the shared component used directly by three surfaces.
- `packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx` (599 lines) — a bespoke popover for the Issue detail default model; it re-enters the shared `ModelSelect` (compact) for its per-stage rows.

Both select on `pointerdown`. The shared one delegates via a **native** `pointerdown` listener on the list container (`ModelSelect.tsx:209-254`) and calls `e.preventDefault()` + `e.stopPropagation()`; the bespoke one attaches an inline `onPointerDown` to each row (`IssueModelSelector.tsx:154-159`). The `preventDefault()` on `pointerdown` is precisely what disables the browser's native touch/pen scroll initiation, and the immediate-select-on-press semantics forces the menu closed the moment a finger touches an option.

This `pointerdown` selection was a workaround introduced in #113 to dodge a Base UI Popover dismiss-timing bug: the popover's dismissable layer fired at the document during the bubble phase of `pointerdown`, closing the popover and removing the option from the DOM **before** the `click` event could reach it — so `onClick` never fired. The current `@base-ui/react` (^1.5.0) no longer has that timing issue, so `click`-based selection now works inside the popover.

Both implementations also hand-roll: the keyboard state machine (`highlightedIndex` + `chipFocus` + `handleKeyDown`/`handleChipKeyDown`, `ModelSelect.tsx:160-161, 266-333`; simpler variant at `IssueModelSelector.tsx:367-383` that even omits the `Escape` its own footer hint promises at line 545), substring/fuzzysort filtering, and the list DOM. There is no scroll-into-view on highlight, no `overscroll-behavior: contain`, group headers are plain non-sticky `<div>`s (`ModelSelect.tsx:410`), and options carry `role="button"` + `tabIndex={-1}` (`ModelSelect.tsx:420-421`) instead of listbox semantics.

The repo already depends on and wraps **cmdk** (`cmdk` ^1.1.1, wrapped in `packages/web/src/shared/ui/components/command.tsx`) and proves it in production via `pages/settings/ui/SettingsSearch.tsx`. Inspection of cmdk's compiled output confirms it: (a) selects on `onClick` + `onPointerMove` + `onKeyDown` only — **no `onPointerDown`, no `preventDefault`**; and (b) sets `role="combobox"` (input), `role="listbox"` (list), `role="option"` + `aria-selected`/`data-selected` (items), `role="group"` (groups), plus `aria-activedescendant`/`aria-controls`/`aria-expanded` on the combobox. This is an almost exact match for the spec's interaction + a11y contract.

Constraints: Web-only change, no API/data-contract/dependency changes (cmdk already installed). Call sites keep their prop contracts. The four `onChange`/`onChangeVariant`/`onChangeModelVariant` callbacks and the `SelectableModel`/`ModelVariantMap` shapes are unchanged (the variant-clearing-on-body-select semantics from #288 are preserved). jsdom cannot exercise real touch scrolling, layout-dependent sticky behavior, or overscroll chaining — those are verified by className/attribute assertions plus manual and browser-track QA, per `design/testing.md`.

## Goals / Non-Goals

**Goals:**

- Selection resolves on `click` (pointer) and `Enter` (keyboard) across both implementations; a press that moves off the option before release does not select.
- Touch/pen dragging scrolls the list natively (no selection logic on `pointerdown`/`touchstart`).
- Scroll is contained inside the popover; provider group headers stay visible while their group is in view.
- Complete, unified keyboard navigation (↑/↓ with scroll-into-view, `Home`/`End`, `Enter`, `Esc`, type-to-filter, chip reach/return).
- Standard combobox + listbox semantics on all four surfaces.
- Identical interaction on every surface via a single shared list implementation.
- Delete the #113 `pointerdown` workaround and the hand-rolled keyboard state machine.

**Non-Goals:**

- No backend, data-contract, model-listing source/content/ordering, or variant data-model changes.
- No Settings/page visual redesign; no new features (favorites, recents beyond what the bespoke popover already has, group collapse).
- No structural merge of `IssueModelSelector`'s extras (loading/error/use-default/recent/fuzzysort/per-stage composite) into `ModelSelect`. Interaction unifies; component boundaries stay.
- No real-touch-scroll / real-overscroll / real-sticky automation in the default `npm test` suite — jsdom cannot exercise them; browser-track QA covers the behavioral layer.

## Decisions

### D1 — cmdk is the list engine for both implementations

Replace the hand-rolled search/filter/keyboard-nav/scroll-into-view in `ModelSelect` and `IssueModelSelector` with cmdk's `Command` primitive set (`Command`, `CommandInput`, `CommandList`, `CommandEmpty`, `CommandGroup`, `CommandItem`), already wrapped in `shared/ui/components/command.tsx`. cmdk gives, for free and verified against its compiled output: `onClick`-based selection (no `pointerdown`/`preventDefault`, so touch scroll is unblocked by construction); `role="combobox"`/`role="listbox"`/`role="option"`/`role="group"` + `aria-selected`/`data-selected`/`aria-activedescendant`/`aria-controls`/`aria-expanded`; built-in ↑/↓/`Home`/`End`/`Enter`/type-ahead; scroll-into-view for the active item; and `CommandGroup`'s `[[cmdk-group-heading]]` for group labels.

The `highlightedIndex` + `chipFocus` state and `handleKeyDown`/`handleChipKeyDown` are deleted from `ModelSelect`; the bespoke `handleKeyDown` in `IssueModelSelector` is deleted (including the promised-but-missing `Escape`, which cmdk handles natively).

**Alternatives considered:**

- *Minimal patch (keep custom list, swap `pointerdown`→`click`, hand-add scroll-into-view / sticky / overscroll / aria).* Rejected: the issue's Product Shape explicitly directs migration onto the library's combobox primitive, and the hand-rolled keyboard/aria surface is exactly the tech debt being retired. A patch would leave two divergent list implementations in place.
- *Base UI `Select` primitive (`shared/ui/components/select.tsx`).* Rejected: it is a select, not a combobox — no search input, no per-row inline-chip layout. Fighting the primitive would cost more than cmdk.

### D2 — Extract one shared internal list component used by both

Introduce a single internal component (working name `ModelOptionList`) that wraps the cmdk composition and owns: overscroll containment on the list, sticky group headings, the chip-inside-item pattern, the `data-model-id` hook for tests, and the empty/loading/error placeholders. `ModelSelect` renders provider-grouped models through it; `IssueModelSelector` renders its Override/Recent/All sections through the same component (sections are just groups). This is the mechanism that guarantees the spec's "identical interaction across all surfaces" — both surfaces literally share one list implementation, so divergence cannot re-creep (the divergence flagged as tech debt by #239 and #408).

Triggers, footers, and surface-specific extras (the per-stage composite under `IssueModelSelector`'s "Per-stage overrides" disclosure, the "Override active" note, the clear button) stay in each parent component. The `IssueModelSelector` default-model popover is **not** restructured into `ModelSelect`; only its list layer swaps to the shared component.

**Alternative considered:** *No shared component — both call cmdk primitives directly with a style/prop convention.* Rejected: a convention will drift again (that is how the current divergence happened). The spec mandates identical behavior, which a shared component guarantees by construction.

### D3 — Selection on `click` via cmdk `onSelect`; delete the `pointerdown` paths

`CommandItem.onSelect` is the selection entry point for both model-body and (propagated-stopped) chip clicks. Delete: the native `pointerdown` delegation listener and its ref-callback lifecycle in `ModelSelect.tsx:209-254, 233-254`; the redundant per-row `onClick` at `ModelSelect.tsx:423` (replaced by cmdk's `onSelect`); and the inline `onPointerDown` on the bespoke `ModelListItem` at `IssueModelSelector.tsx:154-159`. After this, no selection code in either file touches `pointerdown`/`touchstart`, satisfying the spec's "SHALL NOT register selection logic on `pointerdown`".

Because cmdk selects on `click` and the current base-ui no longer dismisses the popover before `click` fires, `onSelect` reaches the handler. The #113 native-listener workaround is no longer needed.

### D4 — Variant chips embed inside each `CommandItem`; isolated by `stopPropagation`

Each model row is one `CommandItem`; its inline `ModelVariantChips` (already `stopPropagation` on both `onPointerDown` and `onClick`, `ModelSelect.tsx:72, 87-93`) lives inside. Chip clicks do not bubble to the item's `onSelect`. Chip keyboard nav (→/`Tab` to enter the chip set, ← at first chip or `Esc` to return, `Enter` to select the variant) stays in chip-scoped `onKeyDown` because cmdk does not manage sub-focus inside items — this stays our code, but it is small and well-bounded. `variantListFor`/`resolveVariantAgainstModel` (`shared/ui/model-variants.ts`) are reused unchanged. The #288 semantics (model-body select clears any prior variant) are preserved in the model `onSelect` handler.

### D5 — Scroll containment + sticky headers via CSS on the list and group

The shared `ModelOptionList` passes className overrides to cmdk: `overscroll-y-contain` on `CommandList` (replacing the `no-scrollbar` default in `command.tsx:113`, which also brings back a visible scrollbar for the long model list — better for discoverability), and `[[cmdk-group-heading]]:sticky [[cmdk-group-heading]]:top-0 [[cmdk-group-heading]]:z-10 [[cmdk-group-heading]]:bg-muted` on `CommandGroup` so the current provider header pins while its group is in view. Both are layout/visual concerns; jsdom tests assert the className is present, and the behavioral effect is verified on the browser track.

### D6 — Search: keep `fuzzysort` in `IssueModelSelector` via cmdk `filter={false}`; `ModelSelect` uses cmdk's built-in filter

`IssueModelSelector` passes `filter={false}` to `Command` and keeps its existing `fuzzysort` result list (`IssueModelSelector.tsx:240-244`); `ModelSelect` uses cmdk's built-in fuzzy filter and drops its hand-rolled substring filter (`ModelSelect.tsx:167-173`). Both satisfy the spec's "typing filters the list." This avoids a search-quality regression on the issue surface without inflating scope.

**Trade-off accepted:** search *algorithm* diverges between surfaces; search *behavior* (type → list filters) is identical. Unifying on cmdk's filter everywhere is an open question (see Open Questions), not a requirement.

### D7 — Combobox + listbox a11y comes from cmdk; verify wiring during build

cmdk already sets the spec-required roles and aria attributes (verified against its compiled output: `role="combobox"` + `aria-controls`/`aria-expanded`/`aria-activedescendant` on the input; `role="listbox"` on the list; `role="option"` + `aria-selected`/`data-selected` on items; `role="group"` on groups with `[[cmdk-group-heading]]` labels). The implementation passes `aria-labelledby` (the existing trigger label) and a meaningful `aria-label` through to `CommandInput`, and confirms via a spec test that `role="listbox"`/`role="option"`/`aria-selected` are present and that `aria-activedescendant` updates when the active item changes. Provider groups are labeled via `CommandGroup`'s `heading` prop (already styled in `command.tsx:142`).

## Risks / Trade-offs

- **[cmdk + Base UI Popover selection timing]** The whole fix rests on the current base-ui not dismissing the popover before `click`. -> Mitigation: the issue's Product Shape states this is verified; add a regression spec test asserting `onChange` fires when an option is `click`ed inside the open popover. If it ever regresses, the fallback is to re-introduce a *click-phase* only handler (never `pointerdown`), which still preserves touch scroll.
- **[Chip-inside-item keyboard conflict]** cmdk's `Enter` on an active item could race a chip's `Enter`. -> Mitigation: chip `onKeyDown` calls `e.stopPropagation()`; cmdk's item `Enter` only fires when the item (not a child chip) holds focus/active state. Add a spec test: focusing a chip and pressing `Enter` selects the variant, not the default-variant model-body.
- **[Search-quality divergence between surfaces]** `IssueModelSelector` keeps `fuzzysort`; `ModelSelect` uses cmdk's filter. -> Accepted as an algorithm-vs-behavior split (see D6); flagged in Open Questions for a later unify decision.
- **[Test churn]** `ModelSelect.test.tsx` (412 lines) and `IssueModelSelector.test.tsx` (593 lines) drive selection via `fireEvent.pointerDown(row)` (e.g. `ModelSelect.test.tsx:137, 162, 196, 323`; scattered in `IssueModelSelector.test.tsx`). All must flip to `fireEvent.click(row)`. -> Mitigation: rewrite methodically per `describe` block; the new tests additionally cover press-move-release no-select, `Home`/`End`, `Esc` on the bespoke popover, listbox roles, and `aria-selected`.
- **[`no-scrollbar` default hides the scroll affordance]** cmdk's wrapped `CommandList` hides the scrollbar (`command.tsx:113`). A long model list with no visible scrollbar hurts discoverability and the spec's "recognizable as a normal dropdown" spirit. -> Mitigation: the shared list overrides to show a scrollbar (D5).
- **[jsdom cannot verify behavioral scroll/sticky/overscroll]** Touch scroll, overscroll containment, and sticky headers have no layout in jsdom. -> Mitigation: unit/spec tests assert the responsible classNames/roles; the actual behavior is verified on the browser track (manual + a browser test if the project adds one), consistent with `design/testing.md`'s separation of jsdom vs. browser concerns.
- **[Two-component structure preserved]** `IssueModelSelector` is not merged into `ModelSelect`. -> Accepted: merging would bloat `ModelSelect` with surface-specific extras (loading/error/use-default/recent) and mix two product shapes. The spec requires identical *interaction*, which D2 delivers without a structural merge.

## Migration Plan

No server migration, no data migration, no feature flag. Web-only; rollback is `git revert`.

Implementation order, in one PR:

1. Add the shared `ModelOptionList` (cmdk-based, with overscroll containment, sticky headings, chip-in-item, placeholders).
2. Port `ModelSelect` onto it (provider grouping via `CommandGroup`; delete `pointerdown` listener, `highlightedIndex`/`chipFocus`, and hand-rolled keydown/filter).
3. Port `IssueModelSelector`'s default-model list onto it (Override/Recent/All as `CommandGroup`s; `filter={false}` + existing `fuzzysort`; delete bespoke `ModelListItem`'s `onPointerDown` and the bespoke `handleKeyDown`).
4. Flip both test files: `fireEvent.pointerDown(row)` → `fireEvent.click(row)`; add the new cases (press-move-release no-select, `Home`/`End`, `Esc` on bespoke, listbox/aria assertions).
5. Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` until green.
6. Manual QA on all four surfaces — **especially real touch scrolling** (DevTools touch emulation or a touch device) and overscroll-boundary behavior at the top/bottom of a long list.

No coordination with runner/server/CLI; they are unaffected.

## Open Questions

- **Unify search algorithm?** Drop `fuzzysort` from `IssueModelSelector` and use cmdk's built-in filter everywhere, or keep the split (D6). Decide by comparing match quality on a real model list during build; the spec does not require either.
- **Browser-track coverage for touch scroll.** Should a Playwright-style browser test be added asserting that a touch drag over the list scrolls without selecting? The repo currently has no browser tests in the default `npm test` (per `design/testing.md`); adding one is consistent with convention but out of scope unless the integrator wants it.
