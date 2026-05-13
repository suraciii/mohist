## Context

The session transcript stack already exists end to end: Mohist persists session stream events, `SessionTranscriptAssembler` reconstructs turns for historical replay, SSE streams live updates, and the Web UI renders assistant parts. Issue #190 improved the surface but left several core model mismatches unresolved. The current backend still tends to coalesce adjacent reasoning and text into long same-type parts, live SSE does not stream thinking chunks, tool lifecycle events can use different ids for start versus update, and file-changing tools are still rendered primarily as raw payloads instead of readable diffs.

This change is constrained by two existing requirements. First, transcript replay and live updates must converge on the same shape so refresh does not materially change ordering, tool identity, or changed-file summaries. Second, the transcript must remain audit-friendly: raw payloads can stay available, but the primary display should be semantic content. The change-local specs in `specs/agent-session-ui/spec.md`, `specs/pipeline-session-events/spec.md`, and `specs/coder-session-tracking/spec.md` define the requirement source of truth alongside the proposal and issue acceptance criteria.

## Goals / Non-Goals

**Goals:**

- Preserve the emitted assistant sequence at the assembler layer so reasoning and text remain interleaved in persisted transcript data.
- Add live `coder_thought_chunk` coverage so running sessions show thinking with the same semantics as replayed sessions.
- Reconcile tool start and update events into one logical tool part even when the upstream stream provides a synthetic id first and a provider id later.
- Render `edit`, `write`, and `apply_patch` tools as diff-first transcript content with changed-file summaries and readable patch details.
- Fix the known transcript display regressions from the #190 review without introducing a second transcript model.
- Reuse existing diff parsing/rendering utilities where possible instead of adding a transcript-only diff stack.

**Non-Goals:**

- No redesign of the overall session page information architecture beyond the targeted transcript fixes.
- No change to coder execution behavior, tool permissions, or upstream opencode event semantics outside the minimal metadata needed for transcript correlation.
- No persistence migration to a new transcript table or snapshot model.
- No attempt to solve every historical transcript inconsistency beyond the bugs called out in this issue.

## Decisions

### D1: Interleaving Is Preserved By Closing The Opposite Active Part Immediately

`SessionTranscriptAssembler` should treat text and reasoning as alternating streams, not append-only buckets. When a text chunk arrives, the assembler should complete any currently open reasoning part before appending text. When a reasoning chunk arrives, it should complete any currently open text part before appending reasoning. Tool parts and terminal/error parts should also close any open streaming text or reasoning part before they are inserted.

This keeps the canonical stored transcript in the order the agent emitted it, which removes the need for frontend reorder heuristics and fixes the root cause of thinking being stacked at the top.

**Alternatives considered:**
- Keep the current giant-part assembly and try to reorder on the frontend. This has already failed because once reasoning and text are merged into long parts, the lost boundaries cannot be reconstructed reliably.
- Split every chunk into its own part. This preserves order but produces extremely noisy transcript data and unnecessary render churn; keeping one open part per active stream is the smaller correct model.

### D2: Live Thinking Uses The Same Transcript Semantics As Historical Replay

Add a first-class `coder_thought_chunk` event through the runtime and SSE pipeline: emission in `session-observers.ts`, registration in `event-bus.ts` and `api/events.ts`, and typed support in `web/src/lib/types.ts` plus `web/src/lib/agent-events.ts`. In `useSessionTranscript.ts`, handle `coder_thought_chunk` with the same close-opposite-part rule used by the assembler so live updates follow the same shape as replay.

The important design choice is not only adding the event, but ensuring the live reducer mirrors the assembler's part-boundary semantics. That keeps live and refreshed transcripts convergent instead of maintaining two subtly different ordering models.

**Alternatives considered:**
- Rely on refresh after completion to reveal thinking. This fails the explicit acceptance criterion that running sessions must show thinking in real time.
- Emit raw reasoning events but let the UI append them as a separate top-level list. This would preserve visibility but would still diverge from the canonical transcript structure.

### D3: Tool Identity Correlation Uses Alias Mapping Between Synthetic And Real IDs

The `unknown orphan` rows are caused by a two-phase identity problem: the tool start event uses a Mohist/session-local synthetic id, while later updates use the provider's real `toolCallId`. The implementation should make one transcript tool part own both identifiers.

Preferred approach:

- When a start event is created before a provider id exists, keep the synthetic id as the local transcript id.
- When an update event arrives with a real provider id, try to match an open tool part by synthetic correlation fields first: same turn, same normalized tool name, same target/path/title when available, and running/open status.
- Once matched, store an alias mapping from real provider id to the existing transcript tool id, and persist the provider id in tool metadata for later direct matches.
- Subsequent updates should resolve through the alias map first, then fall back to the same correlation matcher.

This avoids changing transcript identity mid-stream while still letting later events merge deterministically.

If `agent-session.ts` can cheaply emit the real tool id at start time for some event sources, that is acceptable as an optimization, but the assembler still needs alias logic for historical sessions and mixed payload shapes.

**Alternatives considered:**
- Change `agent-session.ts` to always emit only the real provider id. This is ideal when available but does not cover persisted sessions that already contain synthetic ids, and it assumes every start event knows the final id up front.
- Leave ids mismatched and suppress unknown rows in the UI. This hides the symptom but loses update history and leaves broken changed-file summaries.

### D4: File-Changing Tools Expose Canonical Diff Metadata From The Backend

For `apply_patch`, `edit`, and `write`, the backend should enrich tool metadata with a canonical diff payload. The transcript contract should include:

- `metadata.changedFiles`: normalized file summaries with path, operation, and optional rename source.
- `metadata.diff`: a unified diff string when it can be produced.
- Optional raw tool-specific fields retained for audit/debugging.

Generation strategy:

- `apply_patch`: parse the patch envelope directly and reuse the patch text as the unified diff source.
- `write`: synthesize a unified diff from empty content to the written content.
- `edit`: synthesize a unified diff from old text to new text.

This moves diff extraction to the same backend normalization boundary that already understands tool payload shapes, so the frontend renders one consistent diff view instead of separate raw JSON branches per tool.

**Alternatives considered:**
- Parse each raw payload entirely in the frontend. This duplicates parsing logic across replay and live code paths and keeps the API too raw.
- Compute diffs from git state after the fact. That can drift from the actual tool event when later edits, rebases, or cleanup happen.

### D5: Transcript Diff Rendering Reuses Existing Diff Utilities And Becomes The Primary Semantic View

In `AssistantParts.tsx`, `renderSemanticContent()` should route `displayType === 'diff'` to a dedicated diff content view rather than falling through to raw JSON. The UI should prefer an always-visible semantic block that includes the changed file list and rendered diff, with raw input/output remaining in secondary disclosures.

The frontend should reuse existing diff model/helpers already present in the repo, and may reference the opencode `ContentDiff` interaction pattern, but should avoid importing a whole new transcript-specific parser stack unless current utilities prove insufficient.

This design keeps the semantic representation first-class and aligns edit/write/apply_patch with how users actually inspect file changes.

**Alternatives considered:**
- Keep the current `PatchDiffView` only as an optional collapsed subview under raw JSON. This does not fix the main user problem because the primary rendered content remains unreadable.
- Build a transcript-only custom renderer from scratch. This adds maintenance cost when existing diff utilities already solve most of the parsing and display problem.

### D6: Review Fixes Stay In The Existing Presentation Layer Instead Of Expanding Backend Scope

The two review ERRORs and two WARNs should be fixed at the layer where the bug originates:

- `SearchContentView` ellipsis condition is a straightforward UI logic fix.
- `collectChangedFilesFromTools` should descend into context-group parts so grouped context tools still contribute changed-file summaries.
- `getFallbackSubtitle` duplication should be removed by reusing the shared utility.
- Single-tool context groups should be flattened during the display grouping pass.

These are presentation concerns and do not justify more transcript schema changes. The design keeps the backend focused on canonical part ordering, tool correlation, and diff metadata.

**Alternatives considered:**
- Push grouped changed-file extraction into backend transcript metadata only. That can complement the UI, but it does not address the current frontend summary bug and over-expands this issue's backend scope.
- Leave WARN items for later cleanup. They are small and directly adjacent to the touched transcript code, so fixing them now reduces follow-up churn.

## Risks / Trade-offs

- [Risk] The live reducer and backend assembler drift again over time. → Mitigation: keep the live logic narrowly aligned to the assembler rules, add targeted tests for reasoning/text alternation, and refetch the canonical transcript on completion.
- [Risk] Tool alias correlation may still be ambiguous for concurrent same-name tools. → Mitigation: include target/path/title/status in the fallback matcher and keep unmatched updates visible as warnings rather than silently attaching them incorrectly.
- [Risk] Synthesized diffs for `write` and `edit` may be expensive for large payloads. → Mitigation: generate unified diffs once during normalization, render collapsed by default for large content, and keep raw payloads as fallback.
- [Risk] Historical events may lack enough metadata to resolve every orphan. → Mitigation: preserve the alias matcher as best-effort, expose warnings in metadata, and ensure the common synthetic-id versus real-id case is fixed.
- [Risk] Reusing existing diff utilities may expose format mismatches between issue diffs and transcript tool payloads. → Mitigation: normalize backend `metadata.diff` to a standard unified diff string and add component tests around `apply_patch`, `edit`, and `write` examples.

## Migration Plan

1. Update `SessionTranscriptAssembler` to close opposite active parts on text/reasoning transitions and before tool/error boundaries.
2. Add tool-id alias correlation in the assembler, keeping one logical tool part across start and update events.
3. Enrich file-changing tool metadata with changed-file summaries and unified diff strings.
4. Add `coder_thought_chunk` to the runtime event bus, API event registry, frontend event types, and live transcript hook.
5. Update transcript rendering so diff semantic content is primary, and apply the review fixes in `AssistantParts.tsx` and `session-transcript-display.ts`.
6. Add or update tests for interleaved reasoning/text ordering, live thinking visibility, orphan-tool merging, diff rendering, search ellipsis behavior, and context-group changed-file collection.
7. Validate with `cd packages/cli && npm test` and `cd packages/cli && npm run build`.

Rollback is low risk because this change mostly affects transcript normalization and presentation, not execution semantics or database schema. If regressions appear, the UI can temporarily fall back to raw tool rendering while keeping the enriched metadata and new SSE event in place.

## Open Questions

- Should the alias between synthetic and provider tool ids remain assembler-local only, or should a normalized correlation field also be persisted in tool metadata for easier debugging of historical mismatches?
- For transcript diff rendering, should the first implementation prefer unified view only, or invest immediately in side-by-side rendering if the existing diff utilities already support it cleanly?
