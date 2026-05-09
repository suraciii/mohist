# Review Report

## Result: PASS

The auto-fixes resolved the previously blocking no-id tool correlation issue and the unknown-tool metadata warning gap. I did not find new correctness, security, spec-compliance, or verification failures in the re-review.

## Dimensions

### Correctness: PASS

- Evidence: `packages/cli/src/services/session-transcript-service.ts:816-850` now assigns synthetic ids for no-id tools through pending queues keyed by normalized name and correlation target/title instead of a single raw-name map.
- Evidence: `packages/cli/src/services/session-transcript-service.ts:830-845` merges by correlation first, only uses name-only fallback when exactly one candidate exists, emits `AMBIGUOUS_TOOL_CORRELATION` for name-only fallback, and emits the same warning when a terminal update cannot be safely correlated.
- Evidence: `packages/cli/src/services/session-transcript-service.ts:853-894` keeps pending no-id tool queues consistent across both name and correlation indexes and clears them at each new Mohist prompt at `packages/cli/src/services/session-transcript-service.ts:774-778`, so correlation does not cross conversation turns.
- Evidence: `packages/cli/src/services/session-transcript-service.ts:913-917`, `packages/cli/src/services/session-transcript-service.ts:951-963`, and `packages/cli/src/services/session-transcript-service.ts:1083-1095` now set `hasUnknownTools` and emit `UNKNOWN_TOOL` whenever normalization still resolves to `unknown`, including raw `toolName: "unknown"` events.
- Evidence: Regression tests cover the original blocking scenario at `packages/cli/tests/session-transcript-service.test.ts:1672-1715` and name-only single-candidate warning behavior at `packages/cli/tests/session-transcript-service.test.ts:1717-1745`.
- Evidence: Regression coverage for raw unknown-tool metadata exists at `packages/cli/tests/session-transcript-service.test.ts:1223-1237`.

### Complexity: PASS

- Evidence: The auto-fix adds a small `PendingNoIdTool` structure and three localized queue helpers in `packages/cli/src/services/session-transcript-service.ts:531-535` and `packages/cli/src/services/session-transcript-service.ts:853-894`.
- Evidence: The correlation fix keeps the complex behavior inside `SessionTranscriptAssembler.ensureToolCallId()` and does not expand the public transcript API or frontend rendering contract.
- Note: `SessionTranscriptAssembler.processEvent()` and `useSessionTranscript()` remain long, but the auto-fix did not materially worsen the existing design. Future extraction would be helpful but is not blocking.

### Test Coverage: PASS

- Evidence: `npm test -- session-transcript-service.test.ts session-transcript.test.ts` from `packages/cli` passed with 101 tests.
- Evidence: `npm test -- SessionPage.test.tsx` from `packages/cli/web` passed with 83 tests.
- Evidence: `npm run build` from `packages/cli` passed, including the nested web build.
- Evidence: `npm run build` from `packages/cli/web` passed.
- Coverage now includes the previously missing multiple simultaneous no-id same-name pending tools followed by an id/title/target-less terminal update, plus single-candidate name-only fallback warning and raw `unknown` metadata warning behavior.

### Security: PASS

- Evidence: The auto-fix only changes transcript projection, warning metadata, and regression tests in `packages/cli/src/services/session-transcript-service.ts` and `packages/cli/tests/session-transcript-service.test.ts`.
- Evidence: No new command execution, shell interpolation, credential handling, write-side API behavior, or authorization path was introduced.
- Evidence: The existing session detail route still validates project, issue existence, and session ownership before returning transcript data at `packages/cli/src/api/issues.ts:2063-2101`.
- Note: `npm run build` surfaced existing dependency audit output from `npm --prefix web install` with 1 moderate and 1 high vulnerability, but this auto-fix did not modify dependencies and the build completed successfully.

### Spec Compliance: PASS

- PASS: `coder-session-tracking` / `Tool start and update merge by id`. Stable id merging remains implemented through `toolPartsById` at `packages/cli/src/services/session-transcript-service.ts:924-993` and `packages/cli/src/services/session-transcript-service.ts:1028-1060`, with regression coverage at `packages/cli/tests/session-transcript-service.test.ts:1621-1637`.
- PASS: `coder-session-tracking` / `No-id tool events merge by correlation`. No-id starts are queued by normalized name plus target/title at `packages/cli/src/services/session-transcript-service.ts:822-830` and `packages/cli/src/services/session-transcript-service.ts:853-857`; updates merge by correlation first at `packages/cli/src/services/session-transcript-service.ts:830-833`; ambiguous or missing correlation emits `AMBIGUOUS_TOOL_CORRELATION` at `packages/cli/src/services/session-transcript-service.ts:836-843`.
- PASS: `coder-session-tracking` / `Inferable tools avoid unknown fallback`. Existing inference paths remain in `inferNormalizedToolName()` at `packages/cli/src/services/session-transcript-service.ts:190-305`; unresolved unknowns now correctly surface metadata warnings through `recordUnknownTool()` at `packages/cli/src/services/session-transcript-service.ts:913-917`.
- PASS: `coder-session-tracking` / `Tool status is normalized for transcript display`. Tool update status mapping remains at `packages/cli/src/services/session-transcript-service.ts:1031-1036` and `packages/cli/src/services/session-transcript-service.ts:1106-1110`, with only non-terminal display statuses rendering active.
- PASS: `http-api` / `Detail endpoint returns normalized transcript`. The endpoint assembles canonical transcript data from persisted stream events or fallback logs at `packages/cli/src/api/issues.ts:2103-2139`, and transcript metadata now includes warnings and unknown-tool presence from the assembler at `packages/cli/src/services/session-transcript-service.ts:597-603`.
- PASS: `http-api` / `Historical replay uses persisted data`. Persisted `session_stream_log` rows remain the primary source at `packages/cli/src/api/issues.ts:2103-2106`.
- PASS: `http-api` / `Legacy fallback remains understandable`. Workflow-log fallback is still filtered and reprojected when stream rows are absent at `packages/cli/src/api/issues.ts:2108-2138`, while missing prompt and normalization ambiguity are surfaced through incomplete state or warnings.
- PASS: `http-api` / `Running session metadata is not misleading`. Status-kind derivation avoids treating running/finalizing sessions as completed facts at `packages/cli/src/api/issues.ts:2145-2160`.
- PASS: `pipeline-session-events` / `Live tool updates merge in place`. Live merge logic still updates by tool id and correlation key in `packages/cli/web/src/hooks/useSessionTranscript.ts:239-352`, with terminal refetch at `packages/cli/web/src/hooks/useSessionTranscript.ts:505-507`.
- PASS: `pipeline-session-events` / `Live running state is restrained and accurate`. Live status mapping remains constrained to display states at `packages/cli/web/src/hooks/useSessionTranscript.ts:151-166`, and terminal events reconcile with the canonical API transcript.
- PASS: `pipeline-session-events` / `Terminal events reconcile with persisted replay`. Completion, failure, cancellation, and recovery terminal paths invalidate/refetch the session detail query at `packages/cli/web/src/hooks/useSessionTranscript.ts:512-642`.
- PASS: `pipeline-session-events` / `Live updates respect reader position`. New-content tracking remains implemented through `isNearBottom`, `newContentAvailable`, and `markNewContent()` at `packages/cli/web/src/hooks/useSessionTranscript.ts:354-398`.
- PASS: `agent-session-ui` / `Semantic tool parts`. `SessionTranscriptView` renders Mohist prompt, Coder text/reasoning/tool/error/file-change sections rather than raw event rows at `packages/cli/web/src/components/SessionTranscriptView.tsx:461-547`.
- PASS: `agent-session-ui` / `Context gathering is grouped`. Adjacent context tools are grouped by `ContextGroupCard` at `packages/cli/web/src/components/SessionTranscriptView.tsx:286-380` and the grouping loop at `packages/cli/web/src/components/SessionTranscriptView.tsx:475-489`.
- PASS: `agent-session-ui` / `Bash tools are summarized`. `BashToolCard` shows command, status, duration, output preview, and expandable output at `packages/cli/web/src/components/ToolCallCard.tsx:526-586`.
- PASS: `agent-session-ui` / `File-changing tools show file summaries`. `EditToolCard`, `PatchFilesView`, and backend changed-file extraction expose compact file summaries at `packages/cli/web/src/components/ToolCallCard.tsx:417-524` and `packages/cli/src/services/session-transcript-service.ts:996-1008`.
- PASS: `agent-session-ui` / `Unknown tools have useful fallback display`. Unknown tools now produce warnings regardless of raw source name at `packages/cli/src/services/session-transcript-service.ts:913-917`, while UI raw details remain collapsed in generic cards at `packages/cli/web/src/components/ToolCallCard.tsx:659-711`.
- PASS: `agent-session-ui` / `Readable Mohist coder transcript`. Prompt cards, Coder labels, collapsed reasoning, semantic tools, and compact file outputs remain implemented at `packages/cli/web/src/components/SessionTranscriptView.tsx:42-180` and `packages/cli/web/src/components/SessionTranscriptView.tsx:513-544`.
- PASS: `agent-session-ui` / `The page stays read-only`. The session transcript view renders no composer, continue control, stop button, steering control, or stage-control dashboard.
- PASS: `session-timeline-ui` / `Completed tools render once after refresh`. Historical replay uses merged logical tool parts in the backend assembler and regression tests cover stable id, correlated no-id, ambiguous no-id, and update-only cases.
- PASS: `session-timeline-ui` / `Context and file output remain compact`. Context grouping and turn-level file-change summaries remain present in `SessionTranscriptView` at `packages/cli/web/src/components/SessionTranscriptView.tsx:286-380` and `packages/cli/web/src/components/SessionTranscriptView.tsx:502-544`.
- PASS: `session-timeline-ui` / `Live and historical views agree`. Live updates optimistically merge in place and terminal events refetch the canonical historical transcript.
- PASS: `session-timeline-ui` / `Raw debugging data remains accessible`. Prompt raw text, tool input/output, patch data, and generic raw payload details remain behind explicit disclosure controls in `SessionTranscriptView` and `ToolCallCard`.

## Fix Suggestions

None.

<promise>PASS</promise>
