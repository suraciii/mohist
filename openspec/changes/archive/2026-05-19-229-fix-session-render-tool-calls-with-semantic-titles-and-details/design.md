## Context

The current session transcript pipeline already reconstructs coder activity into turns and tool rows, but the semantic meaning of many tool calls is lost between backend normalization and frontend rendering. The main failure mode is that tool rows are created from early lifecycle events with generic names such as `read`, `bash`, `skill`, or `unknown`, then later `tool_call_update` events bring better titles, targets, outputs, and metadata without fully refreshing the visible display state. The frontend also treats several common tools as generic or internal-only, so useful details that already exist in the payload do not become product-readable transcript content.

This change must preserve the current opencode-like layout, context grouping, and live/replay parity. The design should therefore improve the shared transcript projection contract rather than add a second transcript model or move semantic parsing entirely into the UI.

## Goals / Non-Goals

**Goals:**
- Make tool identity and tool display title stable but updatable across the full tool lifecycle.
- Represent common tool families as first-class semantic transcript concepts with structured details instead of raw JSON-first rendering.
- Surface reviewable mutation content for `edit`, `write`, and `apply_patch` using diff/content views when possible.
- Keep live SSE updates and persisted replay rendering identical by deriving both from the same normalized transcript payload.
- Remove duplicate prompt output-path metadata without redesigning the prompt card.

**Non-Goals:**
- Redesign the overall session transcript page or replace context grouping.
- Build a full standalone diff-review product beyond the existing expandable tool rows.
- Guarantee bespoke renderers for every future arbitrary tool; this change focuses on robust fallback plus first-class support for the common families in the proposal.
- Expose raw stream JSON as the primary UX.

## Decisions

### D1: Backend transcript normalization becomes the single semantic source of truth

The backend should own final tool normalization, title selection, target derivation, and semantic detail extraction. `session-transcript-service.ts` already correlates tool lifecycle events and partially extracts changed files/diff metadata; this design extends that responsibility so the produced transcript payload is already semantically enriched before the web layer projects it.

The core implementation shape is:
- Recompute normalization when `tool_call_update` arrives, not only when the tool row is first created.
- Allow `displayTitle`, `target`, `metadata`, `rawInput`, `rawOutput`, and derived semantic details to become more specific over time.
- Prefer updated semantic fields over placeholder lifecycle titles.
- Treat `skill` and `task` as inferable families even when provider `toolName` is missing or `unknown`, using `title`, `rawInput`, and metadata.

This pulls complexity downward into one deep module and prevents duplicated inference logic between live updates, replay hydration, and multiple frontend components.

**Alternatives considered:**
- Infer most semantics only in React components: rejected because it would split live/replay behavior and repeat parsing logic across renderers.
- Keep current backend model and patch individual renderer titles only: rejected because it would not fix stale `displayTitle`, unknown-tool classification, or payload parity problems.

### D2: Add a structured semantic tool-details contract beside generic transcript fields

The normalized tool payload should keep existing generic fields (`normalizedName`, `displayTitle`, `input`, `output`, `metadata`, `changedFiles`) for compatibility, but add a family-oriented semantic details shape that the UI can consume directly. The structure does not need a separate top-level API response; it can live under `tool.metadata` or a dedicated `tool.details` field as long as it is persisted and replayed intact.

The contract should support at least:
- Context tools: path, pattern, query, include, offset, limit, recursive, and short result summary.
- Mutation tools: affected files, operation, additions/deletions, diff text, before/after content, and write content.
- Execution tools: command, cwd, timeout, exit code, completion status, and bounded output preview.
- Planning tools: todo items, counts by status, and completion summary.
- Delegation tools: subagent type, description, task id, child session id/link.
- Network/interaction tools: URL, query, question prompts, answer count, and result preview.
- Skill tools: skill name plus optional hidden body.

The important design choice is that semantic details are derived once, preserved in the transcript record, and then rendered by family-specific UI components with raw JSON only as fallback.

**Alternatives considered:**
- Store only raw input/output and parse on demand in each renderer: rejected because the same parsing would be duplicated and could diverge.
- Replace existing tool fields with only family-specific data: rejected because the current transcript model and fallback behavior still need generic fields.

### D3: Tool families are rendered through a complete registry, but registry entries consume normalized semantics first

The frontend should continue using the registry approach in `tool-registry.tsx`, but the priority order changes:
1. Use backend-provided `displayTitle` and semantic details when present.
2. Fall back to renderer-specific derivation from raw input/output.
3. Fall back to generic tool family name only as a last resort.

The registry should gain first-class entries for the missing or incomplete families called out in the proposal: `list`, `todowrite`/`todo`, `websearch`, and better `task`, `skill`, `question`, `bash`, `search`, `search_files`. Existing `read`/`glob`/`grep` renderers should stop truncating semantics to a single generic label when structured details are available.

This keeps the UI modular and close to opencode's direction while avoiding a second inference system.

**Alternatives considered:**
- Hardcode special cases directly in transcript row components: rejected because it scatters tool knowledge and makes future additions harder.
- Render every tool through one generic JSON/detail component: rejected because it misses the product goal of semantic readability.

### D4: Mutation tools use a normalized change-view model, not raw input/output, as the primary expansion

`apply_patch`, `edit`, and `write` should converge on one reviewable mutation model. The backend already has `buildUnifiedDiff` and changed-file extraction; this design extends it so each mutation tool produces per-file change entries with path, operation, line-count summary, and either diff text or content blocks. The frontend then renders the same file-change view regardless of whether the source was `patchText`, `oldString/newString`, metadata diff, or a raw write.

Expected behavior by tool family:
- `apply_patch`: parse patch text into per-file entries, preserve moved/created/deleted/modified state, and render expandable patch bodies.
- `edit`: prefer before/after diff from `oldString/newString` or metadata diff; if no diff exists, still show target file and concise content summary.
- `write`: show written content for new-file writes, but prefer a diff when before/after metadata exists.

This keeps one user concept for “what changed?” even though providers emit different mutation payloads.

**Alternatives considered:**
- Keep separate ad hoc renderers for each mutation tool: rejected because it repeats parsing logic and produces inconsistent expansion behavior.
- Show only file lists without content: rejected because the acceptance criteria require reviewable diff/content views.

### D5: Context grouping remains intact, but grouped child rows keep full semantic titles

The current context-group model should stay as-is, but grouped rows must no longer degrade to generic names. The grouping pass in the display projection should preserve each child tool's updated `displayTitle`, subtitle, and structured summary, while the parent group title remains a compact aggregate such as file/query count.

This prevents the grouping layer from discarding the very context targets the backend recovered.

**Alternatives considered:**
- Remove grouping entirely for clarity: rejected by non-goals.
- Keep grouping but show only aggregate parent summaries: rejected because users still need per-tool targets after expansion.

### D6: Prompt metadata deduplication is handled in prompt projection, not in rendering conditionals alone

The prompt output target should be collapsed before rendering by normalizing summary fields during transcript projection. If `subtitle` is effectively the same as `outputPath` or a formatted `Output: <path>` variant, projection should keep one canonical output-target line and suppress the duplicate.

Handling this earlier keeps `PromptBlock` simple and ensures consistent behavior anywhere prompt summaries are reused.

**Alternatives considered:**
- Only compare strings in `PromptBlock`: acceptable as a fallback, but weaker because duplication would remain in the display model and other consumers.

## Risks / Trade-offs

- [Semantic inference remains heuristic for partially malformed tool events] → Prefer backend-provided `toolName` when trustworthy, add ordered fallback inference from title/raw input/metadata, and retain generic fallback rendering instead of failing hard.
- [Adding structured tool details increases transcript payload size] → Keep previews bounded, avoid duplicating full raw payloads into semantic fields, and reuse existing diff/content metadata when already present.
- [Live update recomputation could change a row after it first appears] → This is intended; ensure row identity stays stable by tool call id so only content updates, not row ordering.
- [Mutation normalization across `edit`/`write`/`apply_patch` may expose inconsistent source data] → Define one shared change-view builder with explicit precedence rules: metadata diff first when trustworthy, otherwise patch text, otherwise before/after, otherwise content preview.
- [Todo tools may add visual noise in transcripts with many updates] → Render them compactly with counts and collapsed item lists, while keeping them visible because they carry user-useful progress.

## Migration Plan

1. Extend backend tool normalization so `tool_call_update` refreshes normalized name, display title, target, and semantic details instead of only mutating a subset of raw fields.
2. Introduce shared semantic detail builders for context, mutation, execution, planning, delegation, network, and skill families in the transcript service layer.
3. Persist the enriched transcript payload and confirm replay uses the same normalized structure as live SSE projection.
4. Update frontend display projection to stop suppressing useful todo tools, preserve semantic child rows inside context groups, and deduplicate prompt output metadata.
5. Expand the tool registry to render the supported families from normalized semantic details first, with raw JSON as fallback only.
6. Verify with representative historical sessions from issues #228 and #229 covering skill loads, grouped context tools, mutation tools, bash output, todos, and task delegation.

Rollback is low-risk: the UI can fall back to existing generic tool rendering if semantic detail fields are absent, and backend normalization changes can be reverted without schema migration if the enriched details are stored within the existing transcript payload structure.

## Open Questions

- Should child session navigation for `task` render as a link immediately when a child session id is known, or only after that session has been persisted and is resolvable by the current page router?
- For `bash` output previews, what truncation budget best balances readability and payload size across both live streaming and replayed historical sessions?
