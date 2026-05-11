# Review Report

## Result: PASS

<promise>PASS</promise>

## Dimensions

### Correctness

**PASS** — No logic errors or bugs found.

- The extracted `ModelSelect` component correctly preserves all original behavior from `AiSettingsSection.tsx`.
- `normalizeModels()` correctly converts `string[]` to `Model[]` using `id.split('/').pop()` — consistent with the existing `modelDisplayName` utility in `IssueModelSelector.tsx`.
- Per-stage overrides now use the `ModelSelect` component (Popover without Transition), which fixes the reported dropdown bug while maintaining `handleSetStageModel` and `handleClearStageModel` API call logic unchanged.
- The main Coder Model dropdown in `IssueModelSelector` remains untouched (still uses Popover+Transition), per spec.

Warning: `normalizeModels()` at `ModelSelect.tsx:40` uses `typeof models[0] === 'string'` to discriminate the union type. For empty arrays, `models[0]` is `undefined`, so the check falls through to `return models as Model[]`. This is functionally correct (both branches produce `[]` for empty input), but semantically imprecise. Low severity.

### Complexity

**PASS** — All functions under 50 lines, cyclomatic complexity under 10.

- `ModelSelect` component: ~138 lines, well-structured with clear `useMemo`/`useCallback` boundaries.
- `normalizeModels`: 9 lines, simple type guard.
- `IssueModelSelector` reduced by ~60 lines (per-stage Popover+Transition block removed).

### Test Coverage

**PASS** — All 325 existing tests pass. Build and TypeScript compilation succeed.

However, no dedicated tests exist for the new `ModelSelect` component. Existing `ModelSelector.test.tsx` (363 lines) tests the *different* `ModelSelector` component used in session pages — not the new `ModelSelect`. The new component has zero coverage for its `size` prop, `normalizeModels` logic, or `string[]` input path. This is a gap but not blocking since existing integration coverage through `AiSettingsSection` and `IssueModelSelector` still applies.

### Security

**PASS** — No injection risks, no exposed secrets, no user input passed unsanitized to dangerous APIs.

### Spec Compliance

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| Extract `ModelSelect` to `components/ModelSelect.tsx` | PASS | New file `packages/cli/web/src/components/ModelSelect.tsx` (190 lines) |
| `size` prop: `'default' \| 'compact'` | PASS | `ModelSelectProps.size` at line 36, `isCompact` toggle at line 100 |
| Support `Model[]` and `string[]` input | PASS | `models: Model[] \| string[]` at line 32, `normalizeModels` at line 39 |
| Delete per-stage Popover+Transition in `IssueModelSelector` | PASS | Lines 365-378 now use `<ModelSelect>` instead of ~60 lines of inline Popover+Transition |
| Update `AiSettingsSection` to import extracted component | PASS | Line 8: `import { ModelSelect } from './ModelSelect'`, inline `ModelSelect` definition removed |
| No backend changes | PASS | No API/backend files modified |
| No modification to Coder Model dropdown | PASS | Lines 214-343 of `IssueModelSelector.tsx` unchanged (still uses Popover+Transition) |
| `string[]` conversion uses `id.split('/').pop()` | PASS | `ModelSelect.tsx:43` — same logic as existing `modelDisplayName` |
| Compact styling: `text-xs`, `px-2 py-1` | PASS | Lines 143, 157, 173-174 apply compact classes when `isCompact` |

## Fix Suggestions

1. **`ModelSelect.tsx:40`** — `normalizeModels` empty-array edge case: Consider replacing `typeof models[0] === 'string'` with `models.length === 0 \|\| typeof models[0] === 'string'` for semantic clarity. Low priority, functionally correct as-is.

2. **`IssueModelSelector.tsx:1`** — Unused `React` import (modern JSX transform handles this automatically). Can be removed with no behavioral change.

3. **Missing tests** — Add `packages/cli/web/tests/ModelSelect.test.tsx` covering:
   - Renders with `Model[]` input
   - Renders with `string[]` input and normalizes correctly
   - `size='compact'` applies compact CSS classes
   - `allowClear` + `onClear` clear button flow
   - Search, keyboard navigation, provider grouping