### Requirement: Per-section loading skeletons
While the issue detail page or its sections are loading, the page MUST render per-section skeleton placeholders. It MUST NOT render a single bare "Loading…" line for the whole page.

#### Scenario: Initial page load shows section skeletons
- **WHEN** the issue detail page is loading its initial data
- **THEN** the page MUST render skeleton placeholders in place of the loading sections rather than a bare "Loading…" line

### Requirement: Transient fetch errors are retryable and distinct from not-found
A transient fetch error MUST render an affordance to retry and MUST be visually distinct from the not-found (404) state. A genuinely not-found issue MUST render the not-found state, not the retry state.

#### Scenario: Transient fetch error offers retry and is distinct from 404
- **WHEN** loading the issue fails with a transient error
- **THEN** the page MUST render a retry affordance and MUST be visually distinct from the not-found state

#### Scenario: Not-found issue renders the not-found state
- **WHEN** the requested issue does not exist
- **THEN** the page MUST render the not-found (404) state and MUST NOT render the transient-error retry state
