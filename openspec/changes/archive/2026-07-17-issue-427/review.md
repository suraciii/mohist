# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: cleanup
  Scope: packages/web/src/widgets/session-transcript/ui/tool-views/index.tsx:289
  Evidence: The `[data-testid="tool-row-edit-file-count"]` span carried `data-tone="success"` but renders `text-danger/80` when `isFailed` is true, and the row root already carries `data-tone="danger"` in that case. This is the same class of semantic inconsistency the prior review cycle fixed for the sibling `tool-row-edit-stats` span (prior item-10). Removed `data-tone="success"` so the child no longer contradicts the root tone; the CSS classes (`text-danger/80` / `text-muted-foreground`) continue to control the actual visual styling, so rendering is unchanged.
  Verification: `npm run typecheck -w packages/web` clean; `npm run test:run -w packages/web` - 4735/4735 pass; no test asserts on `data-tone` for this element (verified by ripgrep across `packages/web`).
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/web/src/widgets/session-transcript/ui/PromptBlock.tsx
  Evidence: The prompt collapse/expand behavior (`useState(false)` + conditional render of `<pre>` + "Show full prompt"/"Show less" buttons) is implemented correctly but has no dedicated test that asserts the prompt text is hidden by default and revealed on click. The integration test asserts the prompt block has no bubble classes and fills the column, but does not exercise the expand interaction. This is a coverage gap, not a correctness issue - the behavior is simple and verified manually via the code.
  SuggestedAction: Add a focused test in a `PromptBlock.spec.tsx` or extend `TurnList.render.test.tsx` that renders a turn with a long prompt, asserts the full text is not in the document initially, clicks "Show full prompt", and asserts the text becomes visible.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: packages/web/src/widgets/session-transcript/ui/TurnList.tsx:128-183 (TurnDiffs)
  Evidence: `TurnDiffs` uses hard-coded Tailwind green colors (`bg-green-50/50`, `text-green-700`, `border-green-200`, `text-green-600`, `bg-green-100/50`) instead of the semantic design tokens (`bg-success-subtle`, `text-success`, `border-success-border`) that the rest of this rewrite uses consistently (e.g. `ToolRowView`, `StatusBadge`, `TurnDivider`). This is pre-existing - `TurnDiffs` was not modified by this change (only `TurnHeader`->`TurnDivider` and the `max-w-2xl` removal touched `TurnList.tsx`). Not a regression, but stands out as inconsistent with the rewrite's token usage.
  SuggestedAction: Migrate `TurnDiffs` to semantic success tokens in a future cleanup for visual/token consistency with the rest of the timeline.
  Status: pre-existing

- [ID: item-4]
  Severity: info
  Scope: packages/web/src/widgets/session-transcript/model/session-transcript-display.ts:373-379
  Evidence: In `projectTurn`'s tool-dispatch loop, the branch `else if (!prevIsContext && !currIsContext && topNorm === currNorm)` checks whether the top of `toolStack` is a non-context tool. However `toolStack` only ever receives context tools (the only `toolStack.push` sites are line 371, guarded by `prevIsContext && currIsContext`, and line 385, guarded by `isContextTool(normalizedName)`). So `prevIsContext` is always `true` when `toolStack.length > 0`, making the `!prevIsContext` branch dead code. This is pre-existing logic (the `>=2` guard change did not touch this branch) and does not affect correctness.
  SuggestedAction: Consider removing the dead `!prevIsContext && !currIsContext` branch in a future cleanup.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: packages/web/src/widgets/session-transcript/model/session-transcript-display.ts:313-319
  Evidence: The context-group title count logic only counts `read`/`read_file` (as "reads"), `grep`/`search`/`search_files` (as "searches"), and `glob` (as "globs"). Other context tools in `CONTEXT_TOOL_NAMES` (`list`, `membrowse`, `memread`, `memsearch`) are not counted, so a group of e.g. two `memsearch` calls produces a title of just `"Explored"` with no per-type detail. This is pre-existing - the count logic was the same before this change; this change only added the `>=2` guard and renamed the prefix from "Gathering context" to "Explored".
  SuggestedAction: Extend the count logic to cover all `CONTEXT_TOOL_NAMES` if these memory/list tools are used in practice.
  Status: pre-existing

<promise>PASS</promise>
