## Why

ACP agent sessions silently fail when the context window fills up, completing in seconds without producing expected output. Users get misleading errors like "missing artifact file" with no indication that context exhaustion is the root cause. There is no compaction, no health visibility, and no manual recovery path — leaving users unable to diagnose or recover from one of the most common agent failure modes.

## What Changes

- Pass compaction configuration (threshold, strategy) to ACP server sessions so the agent can auto-compact before hitting context limits
- Capture and display compaction events in the session timeline
- Add a context window usage bar with color-coded health indicator (green/yellow/red) to session pages
- Show warning banners when context usage exceeds 80%
- Add **Compact** and **Reset** buttons to the session page for manual recovery, disabled while the session is active
- Add confirmation dialog for Reset warning about context loss
- Classify `context_exhaustion` as a distinct failure category with improved error messages that suggest Compact or Reset
- Surface context health indicators in workflow session lists and context menus
- Make workflow retry check session health before resuming

## Capabilities

### New Capabilities

- `context-compaction`: Auto-compact threshold configuration, strategy (summary-based), compaction event capture and persistence, context window usage recalculation after compaction
- `session-health-visibility`: Context window usage bar (e.g., "450K / 1M tokens"), color-coded health states (green <60%, yellow 60-80%, red >80%), warning banner when usage exceeds threshold, compact event history in session details
- `session-recovery`: Compact and Reset buttons on session pages, disabled state when session is active, confirmation dialog for Reset, API endpoints for triggering compact/reset, context usage update after recovery
- `context-exhaustion-detection`: `context_exhaustion` failure classification, detection logic based on context window usage data, improved error messages suggesting recovery actions (compact or reset)

### Modified Capabilities

- `agent-runtime`: Accept and forward compaction configuration to ACP sessions; handle compaction events from the ACP server
- `pipeline-session-events`: Emit new SSE event types for compaction events and context health metric updates
- `coder-session-tracking`: Persist context window usage data (`contextWindowSize`, `contextWindowUsed`) and compaction events in session records
- `web-ui`: Session page gains health indicator bar, warning banner, Compact/Reset buttons, confirmation dialog; workflow session lists show health indicators
- `agent-session-ui`: Session transcript surface renders compaction events in the timeline and context health status in page metadata
- `http-api`: New endpoints for compact and reset actions on sessions
- `workflow-run`: Retry path verifies session health before resuming a session

## Impact

- **ACP protocol layer**: New compact configuration fields passed to `createAcpConnection`; new compaction notification handling in session observers
- **Session data model**: New fields for context window metrics and compaction event records
- **Frontend components**: SessionTimeline, session list views, and workflow views gain health and recovery UI
- **API surface**: New session recovery endpoints; existing retry endpoint gains session health pre-check
- **Failure taxonomy**: New `context_exhaustion` failure category in error classification enum
