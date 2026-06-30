## ADDED Requirements

### Requirement: Session-level complete usage summary

The session page SHALL surface a complete session-level usage summary that makes every usage field the server transmits observable in one place: input tokens, output tokens, total tokens, cache-saved (`cachedReadTokens`) tokens, reasoning (`thoughtTokens`) tokens, cost, context-window tokens used, context-window size, context-usage percentage, and health status. The summary SHALL be visible on the session page without requiring the user to navigate to a separate dashboard or issue a request beyond the session metadata the page already loads. The summary SHALL replace the `SessionDetail` dead-stub region (which renders only a literal label and no session data) with a region that presents this substantive session usage, so the region earns its place on the page. When a given usage field is not applicable or unavailable for a session (for example `thoughtTokens` for a non-reasoning model, or `cachedReadTokens` when no cache hit occurred), the summary SHALL present that field gracefully rather than rendering a misleading value.

#### Scenario: All usage fields are visible in one summary

- **WHEN** a session page renders for a session whose server-provided usage includes all usage fields
- **THEN** the session page SHALL display a usage summary covering input, output, total, cache-saved, and thought tokens, cost, context-window used and size, context-usage percentage, and health status
- **AND** the summary SHALL be visible in a single observable region without navigating away

#### Scenario: Cache-saved tokens are surfaced

- **WHEN** a session accrued cache-saved (`cachedReadTokens`) tokens
- **THEN** the usage summary SHALL display the cache-saved token count
- **AND** the value SHALL NOT be silently dropped from the rendered UI

#### Scenario: Reasoning tokens are surfaced

- **WHEN** a session produced by a reasoning model accrued thought (`thoughtTokens`) tokens
- **THEN** the usage summary SHALL display the thought-token count
- **AND** the value SHALL NOT be silently dropped from the rendered UI

#### Scenario: Missing usage fields degrade gracefully

- **WHEN** a usage field is not applicable or unavailable for a session
- **THEN** the summary SHALL present that field gracefully by omission or an explicit not-applicable treatment
- **AND** the summary SHALL NOT render a misleading zero or placeholder value

#### Scenario: Session detail region renders real content instead of a stub

- **WHEN** the session page renders the session-detail region
- **THEN** the region SHALL display substantive session information (usage detail and/or session metadata)
- **AND** the region SHALL NOT render as a placeholder that displays only a literal label and no session data

### Requirement: Token detail in the session observability bar and header row

The session page observability bar / header row SHALL render the complete token detail, including the cache-saved (`cachedReadTokens`) and reasoning (`thoughtTokens`) token counts in addition to the input, output, and total tokens already shown. The cached and thought token counts SHALL be visible alongside the other token metrics in the observability bar so the full token明細 is observable at a glance, rather than being carried in the data model and rendered by zero components.

#### Scenario: Cached and thought tokens appear in the observability bar

- **WHEN** the session page renders the observability bar / header row for a session that accrued cache-saved or thought tokens
- **THEN** the bar SHALL display the cache-saved (`cachedReadTokens`) token count
- **AND** the bar SHALL display the reasoning (`thoughtTokens`) token count
- **AND** both counts SHALL appear alongside the input, output, and total token counts

#### Scenario: Inapplicable token metrics avoid noise

- **WHEN** a session has no cache-saved tokens or no thought tokens (for example a non-reasoning model with no cache hits)
- **THEN** the observability bar SHALL avoid rendering misleading or noisy zero-value metrics for those inapplicable fields
- **AND** the bar SHALL NOT misrepresent a non-accrued metric as an active reading

### Requirement: Usage summary in the sticky session title

The sticky session-title region (the header that remains visible while the transcript body scrolls) SHALL carry a usage摘要 so consumption and context health stay visible during transcript scroll. The摘要 SHALL include at minimum the total token count and the context-usage percentage, so a user can monitor usage and context health without scrolling back to the top of the transcript. The sticky title SHALL continue to display the session title, status, and turn count it already shows.

#### Scenario: Sticky title carries a usage summary while scrolling

- **WHEN** the transcript body is scrolled and the sticky session-title region is visible
- **THEN** the sticky title SHALL display a usage摘要 including at least the total token count and the context-usage percentage
- **AND** the摘要 SHALL remain visible while the transcript scrolls

#### Scenario: Sticky title retains existing identity information

- **WHEN** the sticky session-title region renders with the usage摘要 added
- **THEN** the sticky title SHALL continue to display the session title, status, and turn count
- **AND** the added usage摘要 SHALL NOT displace the existing identity information
