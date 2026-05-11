## Why

Session page renders most tool calls as indistinguishable "unknown" cards because the backend `isKnownToolName` whitelist only recognizes 15 tools and strips everything else (webfetch, task, skill, question, mcp, etc.) to "unknown". The frontend fails to extract meaningful labels or arguments from tool inputs, forcing users to expand raw JSON to understand what each tool is doing. This makes it nearly impossible to follow agent execution progress at a glance.

## What Changes

- Expand or remove the hard `isKnownToolName` whitelist in `session-transcript-service.ts` so original tool names are preserved instead of falling back to "unknown"
- Improve `inferNormalizedToolName` to extract richer tool identity from metadata and input fields (description, query, url, command, etc.)
- Add frontend tool-input summary functions (`getToolLabel`, `getToolArgs`) that extract human-readable descriptions from arbitrary tool inputs
- Redesign `GenericToolCard` to display an icon, "Called {toolName}" header, a subtitle line, and argument tags — instead of raw JSON expansion
- Enhance `SummaryToolCard` with richer parameter extraction for known tools
- Replace static "running..." text with dynamic subtitle that updates based on tool input content
- Refine context tool grouping in `SessionTranscriptView` to be more compact and show per-tool-type counts
- Update `useSessionTranscript` live tool inference to match the relaxed backend normalization

## Capabilities

### New Capabilities

### Modified Capabilities

- `session-timeline-ui` — tool display requirements change: tool cards must show meaningful labels and args for all tools (not just whitelisted ones), context groups must render compactly with per-type counts, and running tools must show dynamic subtitles instead of static text

## Impact

- `packages/cli/src/services/session-transcript-service.ts` — `isKnownToolName` and `inferNormalizedToolName` logic; `ToolPart` type may gain `displaySubtitle` and richer metadata
- `packages/cli/web/src/components/ToolCallCard.tsx` — new summary extraction logic, redesigned `GenericToolCard` and `SummaryToolCard` UI
- `packages/cli/web/src/components/SessionTranscriptView.tsx` — refined `ContextGroupCard` rendering and grouping logic
- `packages/cli/web/src/hooks/useSessionTranscript.ts` — `inferToolName` and `normalizeToolName` must stay consistent with backend normalization changes
