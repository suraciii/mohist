# Self-Review — issue-438 (model selector redo)

Reviewed: `proposal.md`, `specs/model-select/spec.md`, `design.md`, `tasks.json` against the issue and the current codebase (including cmdk ^1.1.1 and `@base-ui/react` ^1.5.0 compiled output).

## What is solid

- **Acceptance-criteria coverage is complete.** All 8 issue ACs map to spec requirements and to task acceptance criteria: AC1/AC2 → Req 1; AC3 → Req 3; AC4 → Req 4; AC5 → Req 5; AC6 → Req 6; AC7 → Req 8; AC8 → Req 7. Traced end-to-end.
- **Non-goals respected.** Model-list source/content/ordering untouched; variant data model reused via `model-variants.ts` unchanged; the bespoke popover's existing Recent section is *preserved* (not introduced), so the "不引入最近使用" non-goal holds.
- **cmdk adoption is well-justified and factually verified.** The design's claim that cmdk selects on `onClick` (no `onPointerDown`/`preventDefault`) and exposes `role="combobox"`/`role="listbox"`/`role="option"`/`role="group"` + `aria-selected`/`data-selected`/`aria-activedescendant`/`aria-controls`/`aria-expanded` is correct — confirmed against `node_modules/cmdk/dist/index.mjs`. cmdk does natively handle `Home`/`End` (also confirmed), so Req 6's Home/End scenario is genuinely free.
- **File/line references are accurate.** Spot-checked `ModelSelect.tsx:209-254, 160-161, 266-333, 167-173, 279-284, 423`, `IssueModelSelector.tsx:154-159, 367-383, 240-244, 545`, `command.tsx:113`, `IssueConfigurationCard.tsx:38` — all match.
- **Task graph is sound.** Vertical-slice split (extract `ModelOptionList` + port `ModelSelect` merged; bespoke popover port; HITL behavioral review). Tests live inside WRITE tasks. DAG is valid and acyclic; every `dependsOn` points to a strictly-lower priority.

## Findings

### F1 — Design over-claims cmdk's keyboard coverage; mis-attributes Escape to cmdk (Moderate)

`design.md` D1/D3 instruct deleting `handleKeyDown`/`handleChipKeyDown` from `ModelSelect` on the basis that "cmdk now provides ↑/↓/Home/End/Enter/type-ahead" (D1) and that "the bespoke `handleKeyDown` … omits the `Escape` … which cmdk now handles natively" (D3). Two inaccuracies:

1. **Escape is handled by base-ui Popover, not cmdk.** cmdk's compiled output handles only `ArrowUp`/`ArrowDown`/`Home`/`End`/`Enter` — `grep` for `Escape`/`Tab` in `cmdk/dist/index.mjs` returns nothing. Escape dismissal actually comes from base-ui's floating-ui `useDismiss` hook, wired at `@base-ui/react/esm/popover/root/PopoverRoot.js:6,117` (`useDismiss` defaults `escapeKey: true`), with `escapeKey` listed in `PopoverRootChangeEventReason`. Deleting `ModelSelect`'s `handleKeyDown` does *not* regress Escape (base-ui covers it before and after), but the design's attribution is wrong and would mislead a builder who relies on cmdk for it.
2. **The bespoke popover's Escape is not actually broken.** The design/proposal claim `IssueModelSelector.tsx:545`'s footer "promises Esc but it is not implemented." Because base-ui `useDismiss` fires on Escape for any base-ui Popover, Escape already works on the bespoke popover today. The "missing Escape" framing is inaccurate; T-002's acceptance criterion ("Escape now closes the popover") will still pass, but the stated rationale (closing a gap) does not reflect reality.

**Impact:** No behavior regression (base-ui covers Escape either way), but the design rationale is factually wrong in two places. **Fix:** Correct D1/D3 to attribute Escape to base-ui Popover's `useDismiss`; remove the "cmdk handles Escape natively" and "bespoke Esc is missing" claims.

### F2 — Deleting `handleKeyDown` removes the only ArrowRight/Tab chip-entry vector; neither cmdk nor base-ui covers it (Moderate)

Spec Req 6 requires "`ArrowRight` or `Tab` from a highlighted variant-capable model row SHALL move focus into that row's chip set". The only current implementation of that entry vector is `ModelSelect.tsx:279-284` inside `handleKeyDown` (the search-input `onKeyDown` catches ArrowRight/Tab when the highlighted model is variant-capable and sets `chipFocus`). cmdk does not handle ArrowRight or Tab (verified — not in its compiled output); base-ui `useDismiss` does not either. cmdk tracks the active item via `aria-activedescendant` (virtual focus on the item, real DOM focus stays on the `CommandInput`), so there is no other element receiving the keystroke that could move focus into the chip set.

`design.md` D4 says "Chip keyboard nav (→/Tab to enter …, ← at first chip or Esc to return, Enter to select the variant) stays in chip-scoped `onKeyDown`." But chip-*scoped* `onKeyDown` only fires *after* a chip has DOM focus — it cannot implement the *entry* from the `CommandInput`. The design instructs deleting `handleKeyDown` (which contains the entry handler) without saying where the entry handler is re-homed.

**Impact:** A builder following D1/D3/D4 literally (delete `handleKeyDown`, put handlers on chips) would regress the spec-required ArrowRight/Tab chip-entry. T-001 acceptance criterion 7 ("→ or `Tab` from a highlighted variant-capable model row moves focus into the chip set") would then fail and force a re-work — the final shipped outcome is protected by the criterion, but the design as written leads the builder into a stumble. **Fix:** D4 (and the T-001 description) must state explicitly that an ArrowRight/Tab handler is retained on the `CommandInput` inside `ModelOptionList` (reading cmdk's active-item id to decide whether to focus the first chip), and that only the `highlightedIndex`/`chipFocus` *state* and the ArrowUp/ArrowDown/Home/End/Enter branches of `handleKeyDown` are deleted (those are what cmdk replaces). The Risk register should add "ArrowRight/Tab chip-entry vector lost if `handleKeyDown` is deleted wholesale" alongside the existing Enter-race entry.

### F3 — `command.tsx`'s wrapped `CommandItem` ships `cursor-default`; spec requires pointer cursor (Minor)

`shared/ui/components/command.tsx:172` styles `CommandItem` with `cursor-default`. Spec Req 5 requires each option to "render with a pointer cursor." T-001's description calls out overriding the `no-scrollbar` default on `CommandList` but does not name the `cursor-default` override on `CommandItem`; only the acceptance criterion (crit 9) implies it. A diligent builder will satisfy crit 9, but the task description under-specifies the override surface. **Fix:** Add the `cursor-default → cursor-pointer` (plus hover/active) override to T-001's description alongside the `no-scrollbar` and sticky-heading overrides.

### F4 — cmdk built-in filter matches `CommandItem.value`; id-based search must be preserved (Minor)

Spec Req 6 "Typing filters" requires matching "displayed text **or id**". The current substring filter matches both `m.name` and `m.id` (`ModelSelect.tsx:167-173`). T-001 switches `ModelSelect` to cmdk's built-in filter but does not state that each `CommandItem`'s `value` must be a composite including the model id (the repo's established pattern is `SettingsSearch.tsx`'s `value={buildHaystack(entry)}`). If `value` defaults to rendered textContent only, id-only queries would stop matching. **Fix:** T-001 description should specify that the `CommandItem` `value` is built from name + id (mirroring `buildHaystack`), so cmdk's filter matches both.

## Verdict

The architecture (cmdk-based shared `ModelOptionList`, click-to-select, scroll containment, sticky headers, combobox a11y, two-task vertical split + HITL review) is sound, and the spec/tasks acceptance criteria enforce the correct outcome. However, the design contains factual inaccuracies that would mislead a builder: Escape is mis-attributed to cmdk (F1), and the instructed wholesale deletion of `handleKeyDown` would remove the only ArrowRight/Tab chip-entry vector that neither cmdk nor base-ui provides (F2). These are problems that must be fixed in `design.md`/`tasks.json` before build.

<promise>FAIL</promise>
