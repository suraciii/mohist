## ADDED Requirements

### Requirement: Session transcript quality regression

Session timeline and session detail surfaces SHALL not expose raw stream fragments as the primary coder-session experience. Regression coverage SHALL prove active and historical sessions remain readable after event normalization, refresh, and live streaming.

#### Scenario: Completed tools render once after refresh

- **WHEN** a persisted session is replayed after refresh
- **THEN** completed tools appear once with stable name, title, status, and details
- **AND** pending/update fragments do not create orphan `unknown running...` entries

#### Scenario: Context and file output remain compact

- **WHEN** a session contains many context tools and file-changing tools
- **THEN** context gathering is grouped into compact summaries
- **AND** file changes are visible as compact transcript output

#### Scenario: Live and historical views agree

- **WHEN** a live session receives streamed text and tool updates and is later refetched from persisted data
- **THEN** the visible transcript order, tool grouping, and file-change summaries remain equivalent

#### Scenario: Raw debugging data remains accessible

- **WHEN** normalized transcript parts hide raw event detail by default
- **THEN** raw prompt, tool input, tool output, and relevant debugging data remain available through explicit disclosure or raw-log access
