# Review

## Findings

1. Error: late `tool_call_update` data still does not refresh the persisted semantic identity or visible title for an existing tool row.
File: `packages/cli/src/services/session-transcript-service.ts:1679-1729`
Evidence: the update path mutates `status`, `title`, `input`, `output`, `error`, and `metadata`, but never recomputes `normalizedName`, `displayTitle`, or `category`. This directly contradicts `coder-session-tracking/spec.md` Scenario "Late tool updates replace generic titles" and the issue evidence about rows getting stuck as `skill`, `bash`, or `unknown`.
Suggested fix: after merging update payload fields, rebuild normalization from the latest tool state and write back `existing.tool.normalizedName`, `existing.tool.displayTitle`, `existing.tool.category`, `existing.tool.target`, and `existing.tool.details` from the recomputed result.

2. Error: `skill` and `task` are still not inferable from semantic payloads when providers omit `toolName`.
File: `packages/cli/src/services/session-transcript-service.ts:316-413`
Evidence: `inferNormalizedToolName()` recognizes `apply_patch`, `edit`, `write`, `read`, `glob`, `grep`, `bash`, `list`, `search`, and `todowrite`, but there is no inference path for `skill` or `task` from `title`, `rawInput`, or metadata. The function therefore still falls back to `unknown` for the exact cases called out in the spec.
Suggested fix: add ordered inference rules for `skill` and `task` based on title patterns (`Loaded skill:`), raw input fields (`name`, `skillName`, `subagent_type`, `description`, `task_id`), and metadata (`skillName`, `childSessionId`, `subagentType`).

3. Error: late semantic payloads are ignored once placeholder raw fields exist, so replay can stay stale even if better input/output arrives later.
File: `packages/cli/src/services/session-transcript-service.ts:1697-1702,1722-1729`
Evidence: `existing.tool.rawInput` and `existing.tool.rawOutput` are only assigned when currently `undefined`. If the start event stored a generic or partial payload, later richer `tool_call_update` input/output is discarded, and `buildSemanticDetails()` keeps reading the stale raw fields.
Suggested fix: replace placeholder raw fields with newer values when the update is more specific, then rebuild `target`, `changedFiles`, diff metadata, and `details` from the final merged state.

4. Error: `write` mutation normalization always reports `created` and ignores richer before/after semantics.
File: `packages/cli/src/services/session-transcript-service.ts:1039-1047`
Evidence: `buildMutationDetails()` hard-codes `operation: 'created'` for every `write`/`write_file` entry and only synthesizes an all-added diff from `content`. The spec requires preferring a diff when before/after metadata exists and not misclassifying modified writes as new-file creates.
Suggested fix: inspect `old_string`/`new_string`, diff metadata, and prior file state fields for `write`, derive `modified` when appropriate, and prefer a before/after diff over unconditional new-file synthesis.

5. Warning: transcript rows can render duplicated semantic text for context tools.
File: `packages/cli/web/src/components/session-transcript/AssistantParts.tsx:887-890,972-976`
Evidence: when a row has no backend `displayTitle`, `toolLabel` is derived from the registry and `fallbackSubtitle` is derived from raw input. For `read`, both can resolve to the same path (`src/a.ts`), which is visible in the failing web test output.
Suggested fix: suppress `fallbackSubtitle` when it equals the chosen tool label.

## Acceptance Criteria

1. FAIL: A completed skill load displays the loaded skill name.
Evidence: backend update flow does not recompute `displayTitle` on later updates (`session-transcript-service.ts:1679-1729`), so an initial generic `skill` label can remain stuck.

2. FAIL: `skill` and `task` events are not reported as `UNKNOWN_TOOL` when semantic payload is sufficient.
Evidence: no `skill`/`task` inference exists in `inferNormalizedToolName()` (`session-transcript-service.ts:316-413`).

3. FAIL: more specific `tool_call_update` semantic data wins over the initial generic data.
Evidence: existing-tool update path never refreshes `normalizedName`/`displayTitle` and preserves stale raw payloads (`session-transcript-service.ts:1679-1729`).

4. PASS: Expanded context groups keep child rows instead of collapsing everything into one aggregate.
Evidence: grouped tools are preserved and rendered through nested `ToolRowView` rows in `packages/cli/web/src/lib/session-transcript-display.ts:313-341,399-400` and `packages/cli/web/src/components/session-transcript/AssistantParts.tsx:1012-1037`.

5. PASS: `bash`/`shell` rows render command/output summaries.
Evidence: execution details are normalized in `session-transcript-service.ts:843-869`; terminal rows render command and output in `AssistantParts.tsx:199-235,905-907`.

6. PASS: `todowrite`/`todo` rows are no longer silently suppressed when present.
Evidence: hidden todo tools are explicitly retained in projection (`packages/cli/web/src/lib/session-transcript-display.ts:367-374`) and rendered via `TodoContentView` (`AssistantParts.tsx:917-919`).

7. PASS: `task` rows can render subagent/task summary when normalized data is already correct.
Evidence: delegation details are built in `session-transcript-service.ts:891-906`; registry and row rendering support task labels in `tool-registry.tsx:150-164` and `AssistantParts.tsx:167-182`.
Note: this is still undermined by Finding 2 for unknown-provider cases.

8. PASS: `question`, `webfetch`, and `websearch` have semantic renderers.
Evidence: interaction details are normalized in `session-transcript-service.ts:908-941`; registry entries exist in `tool-registry.tsx:117-148,316-330`.

9. PASS with warning: `apply_patch` rows expose file-level diff content.
Evidence: patch metadata is normalized in `session-transcript-service.ts:976-1025`; diff rendering uses `DiffContentView` and `PatchDiffView` in `AssistantParts.tsx:481-878,921-929,997-999`.

10. PASS: `edit` rows expose semantic diff content.
Evidence: edit mutation files and synthesized diffs are built in `session-transcript-service.ts:1026-1038`; diff rendering is wired in `AssistantParts.tsx:921-929`.

11. FAIL: `write` rows prefer correct diff/content semantics without misclassifying the operation.
Evidence: `buildMutationDetails()` forces `created` for all writes (`session-transcript-service.ts:1039-1047`), which violates the acceptance criterion for modified writes with before/after metadata.

12. PASS with warning: raw JSON is not the primary UI when semantic renderers are available.
Evidence: execution, read, search, todo, and diff tools all route to semantic views first in `AssistantParts.tsx:896-930`.

13. PASS: prompt output path is deduplicated.
Evidence: projection clears duplicate subtitle/output-path combinations in `packages/cli/web/src/lib/session-transcript-display.ts:150-167`, and `PromptBlock.tsx:60-65` avoids a second output line.

14. PASS: existing context grouping layout remains intact.
Evidence: grouping logic remains in `packages/cli/web/src/lib/session-transcript-display.ts:313-341`.

15. FAIL: persisted replay matches live semantic rendering for late updates.
Evidence: because normalization is not recomputed and richer raw payloads are not adopted on update (`session-transcript-service.ts:1679-1729`), replay can diverge from the intended final live state.

## Complexity

- Warning: `inferNormalizedToolName()` is overly broad and mixes unrelated heuristics in one large branchy function (`packages/cli/src/services/session-transcript-service.ts:316-413`). It is a maintenance hotspot and already missed required `skill`/`task` cases.

## Test Coverage

- FAIL: the required test signal is not green.
- `cd packages/cli && npm test` failed with 15 failing tests, including transcript-adjacent regressions and unrelated suite failures.
- `cd packages/cli/web && npm test` failed with 18 failing tests, including transcript failures such as `tests/session-transcript-display.test.ts` and `tests/SessionPage.transcript.test.tsx`.
- The command example in `tasks.json` also appears stale for this repo: `vitest` rejects `--runInBand`.

## Security

- PASS: no new obvious injection or secret-handling issue found in the reviewed transcript changes.

## Verdict

Overall result: FAIL.

<promise>FAIL</promise>
