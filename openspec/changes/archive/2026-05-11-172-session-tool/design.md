## Context

The Session transcript page suffers from a rigid backend whitelist (`isKnownToolName`) that maps any tool outside 15 hardcoded names to "unknown". The frontend then renders these as indistinguishable `GenericToolCard` components showing only "unknown" with raw JSON buried behind an expand button. Users cannot understand agent execution without clicking every card.

The existing architecture already has:
- `inferNormalizedToolName` in `session-transcript-service.ts` that attempts to identify tools from input/output/metadata
- `getToolCategory` that classifies tools into `context`, `file-change`, `execution`, `planning`
- Frontend `ToolCallCard` with specialized cards for `diff` (edit/write/patch), `terminal` (bash), `summary` (read/glob/grep), and `generic` (fallback)
- `ContextGroupCard` in `SessionTranscriptView` that collapses consecutive context tools
- `useSessionTranscript` that does parallel tool-name inference for live SSE events

The fix must keep the existing `session-timeline-ui` spec invariants: live and historical views must agree, raw data must remain accessible, and completed tools must not create orphan entries.

## Goals / Non-Goals

**Goals:**
- Preserve original tool names instead of collapsing to "unknown"
- Extract and display human-readable labels and argument tags from tool inputs for ALL tools
- Show dynamic running subtitles instead of static "running..."
- Keep context tool grouping compact with per-type counts

**Non-Goals:**
- Full ToolRegistry plugin architecture (out of scope; may be revisited later)
- Changing the backend event schema or database structure
- Adding animations beyond text content changes
- Handling tools that have no input metadata at all (will still show tool name)

## Decisions

### D1: Remove hard whitelist, keep inference as fallback

Remove `isKnownToolName` entirely. `inferNormalizedToolName` will first try to extract a name from metadata/input, then fall back to the original `toolName` field as-is. Only if the original name is literally "unknown" AND no input clues exist will it emit "unknown".

**Rationale:** The whitelist was a premature optimization. The inference logic already handles most tools correctly; the whitelist was throwing away valid names like `webfetch`, `task`, `skill`, `question`. Preserving the original name lets the frontend decide how to display it.

**Alternatives considered:**
- *Expand whitelist to 50+ tools:* Still requires maintenance every time a new tool is added. Doesn't solve the root problem.
- *Keep whitelist but add unknown passthrough:* Adds complexity without benefit. If inference fails, original name is better than "unknown".

### D2: Frontend-first display text extraction

Add `getToolLabel(toolName, input)` and `getToolArgs(toolName, input)` in the frontend. These are pure functions that parse the JSON input and extract human-readable strings. The backend continues to provide `displayTitle`/`displaySubtitle` but the frontend does not depend on them for unknown tools.

**Rationale:** Display text is a presentation concern. Putting it in the frontend keeps the backend focused on data normalization and avoids backend redeploys when we want to tweak display strings.

**Alternatives considered:**
- *Backend computes displaySubtitle for all tools:* Would require backend changes every time display logic changes. Also, `useSessionTranscript` already does its own inference for live events, so frontend duplication would still exist.

### D3: Enrich GenericToolCard instead of adding new card types

The existing card type system (`diff`, `terminal`, `summary`, `generic`) already handles the special cases well. For the "unknown" problem, we improve `GenericToolCard` to show:
- Tool icon (mapped from normalized name or generic fallback)
- "Called {toolName}" header
- Subtitle from `getToolLabel()`
- Arg tags from `getToolArgs()`
- Collapsible raw JSON (still accessible but not the primary view)

**Rationale:** This is the minimal change with maximum impact. Most "unknown" tools will now show meaningful information without needing per-tool UI components.

**Alternatives considered:**
- *Per-tool card registry:* Would require registering each new tool. Rejected as over-engineering for current scope.

### D4: Running subtitle derived from input parsing

When a tool is in `running` state, the card header shows the subtitle extracted from `getToolLabel()` (e.g., "https://example.com" for webfetch, "search: react hooks" for search). If no label is extractable yet, it shows "running..." as fallback.

**Rationale:** Users want to know WHAT is running, not just THAT something is running. The input is usually available at start time, so the subtitle can update immediately.

**Alternatives considered:**
- *Backend sends running subtitle:* Would add latency and complexity to the event pipeline. Frontend parsing is synchronous and instant.

## Risks / Trade-offs

- **[Risk]** Removing the whitelist may expose internal/ugly tool names (e.g., `mcp_server_12345_tool_call`) → **Mitigation:** The normalization function still sanitizes names (lowercase, underscores). The frontend `getToolLabel` will extract meaningful display text, so the raw name is rarely the primary visible text.

- **[Risk]** Frontend input parsing may fail on malformed JSON → **Mitigation:** `getToolLabel`/`getToolArgs` wrap parsing in try/catch and return `undefined`/`[]` on failure, falling back to clean generic display.

- **[Risk]** Backend and frontend normalization drift → **Mitigation:** Both use the same inference heuristics (metadata → input fields → output fields). The backend change is purely to stop overriding valid names with "unknown". `useSessionTranscript` already mirrors the backend inference; after this change it can be simplified to trust the backend `normalizedName` more.

- **[Risk]** Context grouping logic may group non-context tools if whitelist removal changes categories → **Mitigation:** `getToolCategory` is independent of the whitelist. It uses the normalized name, which will now be more accurate, so grouping actually improves.

## Migration Plan

1. Update `session-transcript-service.ts`: remove `isKnownToolName`, adjust `inferNormalizedToolName` to preserve original names
2. Add `getToolLabel`/`getToolArgs` to `ToolCallCard.tsx` (or a new `tool-display.ts` utility)
3. Redesign `GenericToolCard` to use new summary functions
4. Update `SummaryToolCard` running state to use dynamic subtitle
5. Update `useSessionTranscript` to align with backend normalization (simplify inference)
6. Verify with existing sessions that no "unknown" cards remain for tools that previously had valid names
7. Run `npm test` in `packages/cli` and `npm run build` in `packages/cli/web`

Rollback: Revert `session-transcript-service.ts` changes. Frontend changes are backward-compatible because they only add display improvements.

## Open Questions

- Should we create a shared `tool-display.ts` utility module, or keep functions in `ToolCallCard.tsx`?
- Should the backend still set `hasUnknownTools = true` when a tool name cannot be inferred, or is this flag no longer useful?
