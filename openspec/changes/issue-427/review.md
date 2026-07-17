# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/widgets/session-transcript/ui/tool-views/shared.tsx:160
  Evidence: `deriveVerbLedTitle` for the `other` verb family embedded `…` directly into `verb` (`inFlight ? \`${display}…\` : …`) while simultaneously setting `trailingEllipsis: inFlight`. The row JSX in `index.tsx:244-247` appends a second `…` whenever `trailingEllipsis` is true, so any in-flight non-edit/bash/read/search tool (e.g. `task`, `todowrite`, custom tools) rendered `toolName……` (double ellipsis) instead of the design-spec `toolName…` (Decision 5 verb table). Reproduced directly: `render(<ToolRowView part={{normalizedName:'task', status:'running'}}/>)` produced textContent `task……`. This is a typo-class duplicate-character defect; removed the embedded `…` so the JSX appends the single ellipsis via `trailingEllipsis` (consistent with the edit/bash/read/search families, which never embed the ellipsis in `verb`).
  Verification: `npm run test:run -w packages/web` — 4735 tests pass. The tightened test (item-2) now asserts exact `'task…'`.
  Status: resolved

- [ID: item-2]
  Severity: test-gap
  Scope: packages/web/src/widgets/session-transcript/ui/tool-views/tool-row-view.spec.tsx:138
  Evidence: The test "uses the trailing ellipsis for running rows without a recognizable target" asserted `.toMatch(/…$/)` which passes for both `task…` (correct) and `task……` (the double-ellipsis bug), so it could not catch the regression. Tightened to `.toBe('task…')` so the exact single-ellipsis rendering is locked down.
  Verification: `npm run test:run -w packages/web` — the updated assertion passes post-fix (4735/4735).
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: packages/web/src/widgets/session-transcript/ui/index.ts:5
  Evidence: Removing the legacy `TurnToc`/`TranscriptToolbar` exports left the file with no trailing newline (`\ No newline at end of file` in the diff). Restored the trailing newline.
  Verification: `git diff` shows the file now ends with a newline.
  Status: resolved

- [ID: item-4]
  Severity: cleanup
  Scope: packages/web/tests/session-page-test-utils.tsx:96-106
  Evidence: `getAssistantCopyButton` and `expandChangedFilesTool` were only imported by the deleted `tests/SessionTranscriptAffordances.spec.tsx` (T-003 deleted that file per design Decision 9). After deletion they had zero importers (verified by ripgrep across `packages/web`). The `screen` and `fireEvent` imports on line 1 were only used by these two helpers. Removed the dead helpers and the now-unused imports.
  Verification: `npm run typecheck -w packages/web` clean; `npm run test:run -w packages/web` — 4735 pass.
  Status: resolved

## Blocking Items

- [ID: item-5]
  Severity: blocking
  Scope: packages/web/src/pages/session/ui/SessionDetailShell.tsx:11
  Evidence: `SessionDetailShell.tsx` (a `pages/` module) imports the widget-internal module `../../../widgets/session-transcript/model/format-duration` directly, bypassing the widget's public API (`widgets/session-transcript/index.ts`). This violates the project's FSD layer boundary (`pages/` → widget public API only) and fails `npm run check:fsd -w packages/web`:
  ```
  Web FSD boundary violations:
  - pages/session/ui/SessionDetailShell.tsx:11 imports internal widgets/session-transcript module via ../../../widgets/session-transcript/model/format-duration
  ```
  This violation was **introduced by T-001** of this change: master had a local `formatDuration` in `SessionDetailShell.tsx` (no cross-layer import), and `check:fsd` passes clean on `origin/master`. T-001 replaced the local copy with a deep import into the widget's `model/` layer. Because `check:fsd` is part of `test:ci` (and thus the canonical `npm test`), this breaks CI. [disallowed:reason] Repair was considered (re-export `formatDuration` through `widgets/session-transcript/index.ts` and re-route the import) but rejected because adding a new export to the widget's public API is a public-contract change, which the repair policy forbids; the alternative (moving `format-duration.ts` to `shared/lib/`) conflicts with the existing different-signature `shared/lib/format-duration.ts` and constitutes broad refactoring.
  SuggestedAction: Expose `formatDuration` (and `formatElapsed`) from the widget's public API (`packages/web/src/widgets/session-transcript/index.ts`) and change `SessionDetailShell.tsx:11` to import from `'../../../widgets/session-transcript'`. Then re-run `npm run check:fsd -w packages/web`.
  Verification: `npm run check:fsd -w packages/web` must report "checked N production modules" with zero violations.
  Status: open

- [ID: item-6]
  Severity: blocking
  Scope: packages/web/src/widgets/session-transcript/ui/TurnList.render.test.tsx
  Evidence: `TurnList.render.test.tsx` is 323 lines, exceeding the 300-line test-file-size limit with no baseline allowance. `check:test-boundaries` reports:
  ```
  TurnList.render.test.tsx:1:1 test-file-size-budget: has 323 lines, exceeding the 300-line limit without a baseline allowance. Split the file below 300 lines; do not add a new baseline entry.
  ```
  This violation was **introduced by T-002** of this change: the file was 230 lines on `origin/master` (under the 300-line limit) and T-002's rewrite grew it to 323. The progress.txt acknowledges the oversize file but defers it as "not in this task's scope per 'avoid unrelated refactors'" — however the violation is a direct consequence of this change's test rewrite, not pre-existing. `check:test-boundaries` is part of `test:ci` and passes clean on `origin/master`. [disallowed:reason] Repair (splitting the test file) constitutes broad refactoring, which the repair policy forbids.
  SuggestedAction: Split `TurnList.render.test.tsx` below 300 lines — e.g. extract the "TurnDiffs accessibility" describe block (lines 246-323) into a separate `TurnDiffs.test.tsx` or `TurnList.turn-diffs.spec.tsx`. The boundary checker explicitly says "do not add a new baseline entry", so the file must be split, not allowlisted.
  Verification: `npm run check:test-boundaries -w packages/web` must report zero violations for this file.
  Status: open

- [ID: item-7]
  Severity: blocking
  Scope: packages/web/src/widgets/session-transcript/model/session-transcript-display.test.ts
  Evidence: `session-transcript-display.test.ts` is 612 lines, exceeding its 554-line baseline allowance by 58 lines. `check:test-boundaries` reports:
  ```
  session-transcript-display.test.ts:1:1 test-file-size-budget: has 612 lines, exceeding its baseline allowance of 554 lines. Split the file or lower it to at most 554 lines, then lower or remove the baseline entry.
  ```
  This baseline exceedance was **introduced by T-001** of this change: the file was 552 lines on `origin/master` (under its 554-line baseline), and T-001's new ≥2-guard tests (lone-call, multi-call, interruption, lone-call-before-error) grew it to 612. The progress.txt acknowledges the oversize file but defers it as "not in this task's scope" — however the exceedance is a direct consequence of this change's new tests. `check:test-boundaries` passes clean on `origin/master`. [disallowed:reason] Repair (splitting the test file or adjusting the baseline) constitutes broad refactoring / architectural judgment, which the repair policy forbids.
  SuggestedAction: Split the file to ≤554 lines — e.g. extract the context-grouping tests (lone-call, multi-call, interruption scenarios added by T-001, plus the existing grouping tests) into a dedicated `session-transcript-display.context-grouping.test.ts`, then lower or remove the baseline entry per the checker's instruction.
  Verification: `npm run check:test-boundaries -w packages/web` must report zero violations for this file.
  Status: open

## Follow-up Items

- [ID: item-8]
  Severity: follow-up
  Scope: packages/web/src/widgets/session-transcript/ui/AssistantParts.tool-naming.test.tsx:92-104
  Evidence: The test "surfaces a url via FallbackEntry even when the unknown name is not semantically inferred" was weakened from `expect(...textContent).toContain('example.com/page')` to `expect(...textContent?.length ?? 0).toBeGreaterThan(0)`. The original assertion verified the URL was visible on the collapsed row; the replacement is trivially true for any non-empty row. The test name is now misleading — it claims to verify URL surfacing but does not. This reflects a real product change: for `other`-family tools (normalizedName not in edit/bash/read/search), the verb-led title falls back to `toolName` (Decision 5) and `getToolArgs`'s default branch does not extract `url`, so the URL is no longer visible on the collapsed row (only recoverable on expand). This is design-sanctioned per Decision 5, but the test should either be renamed to match what it verifies or updated to assert URL-via-expand.
  SuggestedAction: Either rename the test to reflect the new behavior (e.g. "renders a non-empty row for an unknown tool with a url input; url is recoverable on expand") and assert the expanded content contains the URL, or enhance the `other`-family target extraction to surface URLs if product wants them visible at a glance.
  Status: follow-up

- [ID: item-9]
  Severity: follow-up
  Scope: packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.tsx:39-48
  Evidence: `SessionTranscriptLayoutProps` declares `title: string`, `turnCount: number`, and `statusKind` as required props, and `SessionDetailShell.tsx:387-390` passes all three. However the component destructures only `turns`, `isRunning`, `isThinking`, `isStreaming`, `scrollContainerRef` — `title`, `turnCount`, and `statusKind` are never read. This is dead surface area in the public contract; callers are forced to supply values that are ignored.
  SuggestedAction: Either remove the unused props from the interface (and stop passing them from `SessionDetailShell`), or use them (e.g. render the title in the column header near `CopyFullTextButton`).
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: packages/web/src/widgets/session-transcript/ui/tool-views/index.tsx:276
  Evidence: The `[data-testid="tool-row-edit-stats"]` span is marked `data-tone="success"` but contains both `+N` (green `text-success`) and `−M` (red `text-danger`). The container-level `data-tone="success"` is semantically misleading since the span also carries deletion data. Minor — does not affect rendering, only the semantic attribute.
  SuggestedAction: Drop the `data-tone="success"` from the container span (the child `+N`/`−M` spans already carry their own color classes), or omit `data-tone` entirely on the stats container.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: info
  Scope: packages/web/src/widgets/session-transcript/ui/TurnList.tsx:128-183 (TurnDiffs)
  Evidence: `TurnDiffs` uses hard-coded Tailwind green colors (`bg-green-50/50`, `text-green-700`, `border-green-200`, `text-green-600`) instead of the semantic design tokens (`bg-success-subtle`, `text-success`, `border-success-border`) that the rest of this rewrite uses consistently (e.g. `ToolRowView`, `StatusBadge`). This is pre-existing — `TurnDiffs` was not modified by this change (only `TurnHeader`→`TurnDivider` and the `max-w-2xl` removal touched `TurnList.tsx`). Not a regression, but it stands out as inconsistent with the rewrite's token usage.
  SuggestedAction: Migrate `TurnDiffs` to semantic success tokens in a follow-up for visual/token consistency with the rest of the timeline.
  Status: pre-existing

- [ID: item-12]
  Severity: info
  Scope: packages/web/src/widgets/session-transcript/model/session-transcript-display.ts:373-379
  Evidence: In `projectTurn`'s tool-dispatch loop, the branch `else if (!prevIsContext && !currIsContext && topNorm === currNorm)` checks whether the top of `toolStack` is a non-context tool. However `toolStack` only ever receives context tools (the only `toolStack.push` sites are line 371, guarded by `prevIsContext && currIsContext`, and line 385, guarded by `isContextTool(normalizedName)`). So `prevIsContext` is always `true` when `toolStack.length > 0`, making the `!prevIsContext` branch dead code. This is pre-existing logic (the `≥2` guard change in T-001 did not touch this branch) and does not affect correctness — just dead code that could confuse future readers.
  SuggestedAction: Consider removing the dead `!prevIsContext && !currIsContext` branch in a future cleanup, or document why it exists if there is a non-obvious path.
  Status: pre-existing

<promise>FAIL</promise>
