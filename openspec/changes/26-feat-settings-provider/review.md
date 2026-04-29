# Review Report

## Result: FAIL

## Dimensions

### Correctness: FAIL

**ERROR: Duplicate label rendering in ProviderGroup**

- `packages/cli/web/src/components/ProviderGroup.tsx:26` — The `label` is already rendered with count inside `ProviderGroup` (`{label} ({count})`), but `useProviderGroups.ts:67` already embeds the count into the label string (`label: \`${def.label} (${items.length})\``). This causes the group title to render as **"Recommended (6) (6)"** — the count is duplicated.
- `packages/cli/web/src/components/SettingsPage.tsx:462-478` — There is a hardcoded "Custom" section with an "Add Custom Provider" button that exists **outside** the grouped provider rendering loop. If a user has custom providers (isBuiltin=false), they will appear in the `ProviderGroup` for "Custom" via `useProviderGroups`, but the "Add Custom Provider" button and description text are hardcoded separately. The two Custom sections are disconnected — the grouped custom providers render above the `<hr>`, while the "Add Custom Provider" button is below it, breaking the visual grouping.

**WARNING: Tests are broken**

- `packages/cli/web/tests/SettingsPage.test.tsx:94,99,104,125` — All 9 tests fail because: (1) the test wrapper doesn't include a `<Router>` context (the component now uses `useNavigate`), and (2) the tests assert for old section names (`"Connected Providers"`, `"Available Providers"`, `"Custom Providers"`) that no longer exist in the refactored component. The new grouped view renders `"Connected (N)"`, `"Recommended (N)"`, etc.

### Complexity: PASS

- All functions are concise and focused. `assignGroupKey` (6 lines), `useProviderGroups` hook (44 lines), `ProviderGroup` component (43 lines), `useDebouncedValue` (9 lines).
- No function exceeds 50 lines. Cyclomatic complexity is low throughout.
- `SettingsPage.tsx` at 505 lines is large but mostly JSX templates for loading/error/tab states. The main render logic is clean.

### Test Coverage: FAIL

- No new tests were added for the new modules (`provider-categories.ts`, `useProviderGroups.ts`, `ProviderGroup.tsx`).
- Existing `SettingsPage.test.tsx` is broken (all 9 tests fail) and was not updated to match the refactored component.
- No test coverage for: search functionality, debounce, Escape key, group ordering, group collapsing/expanding, category assignment logic, fuzzysort filtering, empty state messages.

### Security: PASS

- No security concerns. Search input is purely client-side string filtering. No SQL injection, XSS, or credential exposure risks.
- Fuzzysort operates on in-memory data only.

### Spec Compliance: FAIL

#### T-001: Create provider category mapping and types

| Criterion | Result | Notes |
|-----------|--------|-------|
| PROVIDER_CATEGORIES maps at least 30 provider IDs | **PASS** | 84 providers mapped |
| Recommended providers have category='recommended' | **PASS** | openai, anthropic, deepseek, google, groq, mistral |
| Coding plan providers have category='coding-plan' | **PARTIAL** | Spec lists `minimax-for-coding`, implementation uses `minimax-coding-plan`. Also spec lists `kimi-for-coding` — implementation uses `kimi-for-coding` which is correct. The spec's provider IDs may not match actual provider IDs in the snapshot, but `minimax-for-coding` from spec is mapped as `minimax-coding-plan` in code — this appears intentional to match actual IDs. |
| TypeScript types exported | **PASS** | `ProviderCategory`, `ProviderRegion`, `ProviderCategoryInfo`, `GroupedProvider` |
| Typecheck passes | **PASS** | Confirmed |

#### T-002: Create useProviderGroups hook

| Criterion | Result | Notes |
|-----------|--------|-------|
| Groups in correct order | **PASS** | connected, recommended, coding-plan, china, international, custom |
| Each provider in exactly one group | **PASS** | `assignGroupKey` returns single key by priority |
| configured → connected | **PASS** | Line 24 |
| isBuiltin===false → custom | **PASS** | Line 25 |
| Unmapped builtin → international | **PASS** | `getProviderCategory` defaults to international |
| Empty groups excluded | **PASS** | Line 64: `if (items && items.length > 0)` |
| Search filtering via fuzzysort | **PASS** | Lines 38-42 |
| Searching auto-expands groups | **PASS** | `expanded={isSearching \|\| undefined}` in SettingsPage:456 |
| Clearing search restores collapsed | **PASS** | When `isSearching` is false, `expanded` is `undefined` so internal state controls |
| Typecheck passes | **PASS** | Confirmed |

#### T-003: Create ProviderGroup collapsible component

| Criterion | Result | Notes |
|-----------|--------|-------|
| Group title shows 'Label (count)' | **FAIL** | Title renders as `{label} ({count})` but `label` already contains `(${items.length})` from useProviderGroups. Results in double count: "Recommended (6) (6)" |
| Default shows max 5 providers | **PASS** | `DEFAULT_VISIBLE_COUNT = 5` |
| 'Show all (N)' when >5 | **PASS** | Line 38 |
| Click toggles expand/collapse | **PASS** | Lines 35-39 |
| Groups with ≤5 show all, no toggle | **PASS** | Line 19: `showToggle = count > DEFAULT_VISIBLE_COUNT` |
| expanded prop forces show all | **PASS** | Line 18: `isExpanded = forceExpanded ?? internalExpanded` |
| Empty groups render nothing | **PASS** | Line 16 |
| Typecheck passes | **PASS** | Confirmed |

#### T-004: Refactor SettingsPage with search and grouped provider list

| Criterion | Result | Notes |
|-----------|--------|-------|
| Search input at top of providers tab | **PASS** | Lines 414-435 |
| Real-time filtering across all groups | **PASS** | Via useProviderGroups with debouncedSearch |
| 300ms debounce | **PASS** | `useDebouncedValue(searchInput, 300)` |
| Escape key clears search | **PASS** | Lines 251-256 |
| Empty results show 'No providers found matching your search' | **PASS** | Lines 437-442 |
| Searching auto-expands matching groups | **PASS** | `expanded={isSearching \|\| undefined}` |
| Clearing search restores collapsed state | **PASS** | isSearching becomes false |
| Provider connect/remove/test actions work | **PASS** | renderProviderCard preserves card components |
| ProviderConnectDialog opens on connect | **PASS** | Lines 493-497 |
| CustomProviderDialog opens from Custom group's add button | **FAIL** | The "Add Custom Provider" button is in a separate hardcoded section below the groups loop, not inside any ProviderGroup. Spec says "from the Custom group's add button" — the button is not inside the Custom group. |
| Connected providers appear in top group with remove | **PASS** | Connected group is first in GROUP_ORDER |
| Typecheck passes | **PASS** | Confirmed |
| Build passes | **PASS** | Confirmed |

## Fix Suggestions

1. **`packages/cli/web/src/components/ProviderGroup.tsx:26`** — Remove the duplicate count from the label rendering. Change `{label} ({count})` to just `{label}` since the count is already embedded in the label string by `useProviderGroups`.

2. **`packages/cli/web/src/components/SettingsPage.tsx:462-478`** — Move the "Add Custom Provider" button inside the Custom `ProviderGroup` rendering, or integrate it into the ProviderGroup component. The hardcoded Custom section should not exist separately from the grouped Custom providers.

3. **`packages/cli/web/tests/SettingsPage.test.tsx`** — Update test wrapper to include `<MemoryRouter>` from react-router. Update assertions to match new group label format (e.g., `"Connected (1)"` instead of `"Connected Providers"`). Add tests for search, debounce, group ordering, and collapse/expand behavior.

4. **`packages/cli/web/tests/SettingsPage.test.tsx` or new test files** — Add unit tests for `provider-categories.ts`, `useProviderGroups.ts`, and `ProviderGroup.tsx` to cover category assignment, search filtering, and expand/collapse behavior.
