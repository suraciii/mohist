## Review: Issue #220 — Refactor session UI to remove duplicate legacy tool rendering logic

### Correctness

**PASS** — The refactoring correctly removes all duplicated parsing logic from `ToolCallCard.tsx` and replaces it with imports from the shared `transcript-tool-utils.ts`. Verified:

- `ToolCallCard.tsx` no longer defines `parseJsonSafely`, `getToolLabel`, `getToolArgs`, `TOOL_DISPLAY_TYPE`, `parsePatchOperations`, `parseEditInput`, `parseEditWriteChanges`, `getDisplayType`, or `extractPatchTarget` locally (grep confirms zero matches).
- `ToolCallCard.tsx:4-13` imports all shared helpers from `../lib/transcript-tool-utils`.
- `ToolCallCard.tsx:679` uses `getDisplayType(entry.toolName)` for display-type routing instead of a local lookup.
- `ToolCallCard.tsx:272` uses `parseEditInput(entry.rawInput)` from shared utils.
- `ToolCallCard.tsx:275-276` uses `parsePatchOperations` and `parseEditWriteChanges` from shared utils.
- `ToolCallCard.tsx:447-448` uses shared `getToolLabel` and `getToolArgs` in `SummaryToolCard`.
- `ToolCallCard.tsx:601-602` uses shared `getToolLabel` and `getToolArgs` in `GenericToolCard`.
- Re-exports on line 15-16 (`export { getToolLabel, getToolArgs, parsePatchOperations, parseEditInput }`) maintain backward compatibility for any consumer importing from `ToolCallCard`.
- `SessionTranscriptView.tsx:4` only imports `ToolCallCard` from `ToolCallCard.tsx` — no parsing functions — so no compatibility break.

No logic errors, edge-case regressions, or off-by-one issues observed. The shared implementations in `transcript-tool-utils.ts` are identical to what was removed from `ToolCallCard.tsx` (verified by diff comparison).

### Complexity

**PASS** — `ToolCallCard.tsx` reduced from 983 lines to 704 lines (~28% reduction). All functions remain under 50 lines. No high-cyclomatic-compatibility functions introduced. The file now contains only presentation components (`PatchBlock`, `DiffBlock`, `TerminalBlock`, `EditToolCard`, `BashToolCard`, `SummaryToolCard`, `GenericToolCard`, `ToolCallCard`, plus small helpers), with zero parsing ownership.

### Test Coverage

**PASS** — New test file `packages/cli/web/tests/shared-tool-semantics.test.ts` (311 lines, 47 tests) provides comprehensive regression coverage:

- **Display type consistency** (4 tests): Verifies `getDisplayType` and `getToolDisplayType` agree for terminal, diff, summary, and generic tools.
- **Label consistency** (16 tests): Covers bash, read, grep, glob, webfetch, task, skill, edit, write, question with various input shapes.
- **Badge/args consistency** (11 tests): Covers bash (timeout, cwd), read (recursive, include), grep (type, scope), webfetch (method), edit/write (edit flag), task (priority).
- **Patch/file-change parsing** (7 tests): Covers `parsePatchOperations` (add, update, delete, multi-file), `parseEditInput`, `parseEditWriteChanges`.
- **Fallback subtitle** (4 tests): Covers `getFallbackSubtitle` for description, url, filePath, and invalid inputs.
- **Registry entry consistency** (3 tests): Verifies registry entries delegate to shared helpers.

All 47 tests pass. Pre-existing test failures in `session-transcript-display.test.ts` (5) and `SessionPage.transcript.test.tsx` (5) are confirmed to exist on master and are unrelated to this change (no diff on those files).

### Security

**PASS** — No new input handling, API calls, or secret exposure. The change only moves existing parsing functions from one file to another. No injection risks introduced.

### Spec Compliance

#### Acceptance Criterion 1: `ToolCallCard` no longer retains duplicate tool parsing functions

**PASS** — Verified by grep: zero local definitions of `parseJsonSafely`, `getToolLabel`, `getToolArgs`, `TOOL_DISPLAY_TYPE`, `parsePatchOperations`, `parseEditInput`, `parseEditWriteChanges`, `getDisplayType`, or `extractPatchTarget` in `ToolCallCard.tsx`. All are imported from `transcript-tool-utils.ts`.

#### Acceptance Criterion 2: Legacy session view and registry path use the same label/args/display type/patch parsing rules

**PASS** — `ToolCallCard.tsx:4-13` imports from `transcript-tool-utils.ts`. `tool-registry.tsx:3-12` imports from the same `transcript-tool-utils.ts`. Both `getDisplayType` and `getToolDisplayType` (registry) ultimately call the same shared `getDisplayType` function. Test file `shared-tool-semantics.test.ts` explicitly asserts equality between legacy and registry paths for all representative tools (47 assertions pass).

#### Acceptance Criterion 3: Existing session transcript page behavior is compatible

**PASS** — `SessionTranscriptView.tsx` imports only `ToolCallCard` from `ToolCallCard.tsx` (line 4) and passes `ToolCallEntry` objects unchanged. The `ToolCallCard` component's external API (props interface) is unchanged. Frontend build passes (`tsc -b && vite build` succeeds).

#### Acceptance Criterion 4: New tool display rules only need modification in `transcript-tool-utils` or `tool-registry`

**PASS** — `ToolCallCard.tsx` no longer contains any tool-specific parsing rules. All label extraction, argument badge generation, display type classification, and patch/file-change parsing are centralized in `transcript-tool-utils.ts` (lines 10-311) and `tool-registry.tsx` (which delegates to the same shared helpers).

#### Acceptance Criterion 5: Related frontend tests and existing build pass

**PASS** — Frontend build passes (0 errors, 0 warnings). All 47 new tests pass. Pre-existing test failures are unrelated to this change.

### Warnings

1. **Re-exported symbols from ToolCallCard** (`ToolCallCard.tsx:15-16`): The file re-exports `getToolLabel`, `getToolArgs`, `parsePatchOperations`, `parseEditInput`, `ToolDisplayType`, and `EditInput` for backward compatibility. Currently only `SessionTranscriptView.tsx` imports from `ToolCallCard`, and it only uses `ToolCallCard` (the component). These re-exports are safe but technically unused — consider removing them in a future cleanup if no external consumers are found.

2. **ToolIcon local map** (`ToolCallCard.tsx:520-595`): The `ToolIcon` component maintains a local SVG icon map that duplicates icons in `tool-registry.tsx`. This is acceptable per design decision D3 (icons are secondary to parsing dedup) but remains a minor duplication point.

3. **`parseBashInput` remains local** (`ToolCallCard.tsx:53-63`): This is a simple JSON extraction specific to the bash card presentation, not a semantic parsing rule, so keeping it local is appropriate.

<promise>PASS</promise>
