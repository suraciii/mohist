## ADDED Requirements

### Requirement: Server-provided context health is the source of truth

The UI SHALL consume the server-provided `healthStatus` classification (green/yellow/red) and `contextUsagePercent` as the authoritative source of context health, rather than recomputing the context-usage percentage or health classification client-side from the context-window ratio. The client SHALL NOT re-derive a context-health classification that duplicates or contradicts the server-provided classification in any surface — the session-page observability bar, the shared `ContextHealthIndicator`, or the workflow-sessions panel rows. The redundant client-side recomputation (in `context-health.ts` and the consuming surfaces) SHALL be removed so a single source of truth governs the rendered health. When the server does not provide `healthStatus` or `contextUsagePercent` for a session, the UI SHALL degrade gracefully (for example by hiding the indicator) rather than rendering a stale or fabricated classification.

#### Scenario: UI consumes server-provided health status

- **WHEN** the server provides a `healthStatus` classification for a session
- **THEN** the UI SHALL render that classification directly
- **AND** the UI SHALL NOT recompute a conflicting classification client-side from the context-window ratio

#### Scenario: UI consumes server-provided context-usage percent

- **WHEN** the server provides a `contextUsagePercent` for a session
- **THEN** the UI SHALL display that percentage directly
- **AND** the UI SHALL NOT recompute the percentage client-side from the context-window ratio

#### Scenario: Single source of truth across surfaces

- **WHEN** the same session's context health is rendered in the session-page observability bar, the `ContextHealthIndicator`, and a workflow-sessions panel row
- **THEN** every surface SHALL derive its classification and percentage from the same server-provided values
- **AND** no surface SHALL recompute its own classification independently

#### Scenario: Missing server values fall back gracefully

- **WHEN** the server does not provide `healthStatus` or `contextUsagePercent` for a session
- **THEN** the UI SHALL degrade gracefully by hiding the indicator or omitting the reading
- **AND** the UI SHALL NOT render a stale or fabricated classification
