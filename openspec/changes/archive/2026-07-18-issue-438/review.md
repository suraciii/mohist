# Review Findings

## F1 - Issue default selector does not use the shared list primitive

`packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx:421-560` reimplements the cmdk root, input, list, groups, items, chip refs, and keyboard handlers instead of rendering `ModelOptionList`. This contradicts the approved design D2 and task T-002, which require the Issue detail default-model popover to use the single shared implementation so interaction cannot diverge across the four surfaces. It has already diverged: the Issue version has a separate `handleChipKeyDown`/`handleCommandKeyDown` and direct list composition, while `ModelOptionList` owns different copies. Move the Issue-specific sections (Override, Recent, loading/error) into a supported shared-list composition or otherwise refactor so both selectors actually share the list interaction layer.

## F2 - Filtering the issue default selector can leave `aria-selected` pointing at a non-rendered option

`packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx:421` passes `value={configuredModel ?? undefined}` to cmdk even when `searchQuery` makes `displayedModels` omit that model (`185-190`, `513-554`). cmdk uses its controlled value as the active item, so after filtering for a different model the combobox's `aria-activedescendant` can reference the configured model's item id even though that item is no longer rendered. Arrow/Enter selection is likewise driven by the stale active value instead of a visible filtered option. This violates the keyboard-navigation and combobox requirements: after filtering, the highlighted option must be in the filtered list and `aria-activedescendant` must reference it. Reset/select a visible active item when the external fuzzysort result changes, or make the cmdk selection state reflect the current rendered list; add a regression test that starts with one selected model, filters it out, then verifies Arrow/Enter and `aria-activedescendant` target a visible result.

## F3 - Issue default selector options lack an explicit hover/active visual state

The Issue selector's rows at `packages/web/src/features/select-issue-model/ui/IssueModelSelector.tsx:481-484` and `530-533` have `cursor-pointer` but no hover or active/pressed styling. Unlike `ModelOptionList`, they also do not include cmdk's `data-selected:bg-muted` state for the keyboard-highlighted option. The acceptance criteria require pointer cursor, hover, and active/pressed affordances on every selector surface, and require identical interaction/visual behavior across the shared and Issue selectors. Apply the same row state classes as the canonical shared component and cover the Issue default-model popover in tests.

<promise>FAIL</promise>
