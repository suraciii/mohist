### Requirement: Authoritative retryable failure-category allowlist

The system SHALL define exactly one authoritative failure-category retryability allowlist. A failed Turn's recorded failure category is retryable if, and only if, it is one of: `runner-unavailable`, `runner-lost`, `report-timeout`, `deadline`, `timeout`, `probe-timeout`, `runtime-transport-unavailable`, `rate-limited`, or `retry-safe`. Every other recorded category — including `input`, `permission`, `configuration`, `context`, `unknown`, and a generic turn failure — MUST be classified as not retryable.

#### Scenario: Recorded category is in the allowlist

- **WHEN** a failed Turn's authoritative failure facts record a failure category of `runner-unavailable`, `runner-lost`, `report-timeout`, `deadline`, `timeout`, `probe-timeout`, `runtime-transport-unavailable`, `rate-limited`, or `retry-safe`
- **THEN** the failed Turn MUST be classified as retryable

#### Scenario: Recorded category is a permanent failure

- **WHEN** a failed Turn's authoritative failure facts record a failure category of `input`, `permission`, `configuration`, `context`, `unknown`, or a generic turn failure
- **THEN** the failed Turn MUST be classified as not retryable

#### Scenario: Recorded category is absent

- **WHEN** a failed Turn's authoritative failure facts record no failure category
- **THEN** the failed Turn MUST be classified as not retryable

#### Scenario: Recorded category is not in the allowlist

- **WHEN** a failed Turn's authoritative failure facts record a category value that appears in neither the retryable allowlist nor the known permanent list
- **THEN** the failed Turn MUST be classified as not retryable

### Requirement: Retryability is decided from recorded category facts only

The system MUST decide retryability exclusively from the failed Turn's recorded failure category. It MUST NOT infer, guess, or derive retryability from failure reason text, error message text, exit codes, or any heuristic over raw error output.

#### Scenario: Transient-sounding error text with a permanent category

- **WHEN** a failed Turn records a permanent category such as `input` while its failure reason or error text mentions transient-sounding words like "unavailable", "timeout", or "retry"
- **THEN** the failed Turn MUST be classified as not retryable
- **AND** no retry affordance or retry acceptance MUST be granted on the basis of that text

#### Scenario: Retry-safe category with ordinary text

- **WHEN** a failed Turn records the failure category `retry-safe` regardless of how ordinary its error text reads
- **THEN** the failed Turn MUST be classified as retryable

### Requirement: Retryability is re-evaluated at click acceptance

At Retry click acceptance the system MUST re-read the target Turn's current authoritative failure facts and re-apply the same allowlist. A target that no longer presents a failed Turn with a currently retryable recorded category MUST be rejected as no longer retryable.

#### Scenario: Target facts changed after presentation

- **WHEN** a Retry click arrives for a Turn whose current authoritative failure facts no longer record a retryable failure category (for example the category was superseded by a permanent one, or the Turn no longer resolves as failed)
- **THEN** the click MUST be rejected with an explicit no-longer-retryable outcome
- **AND** no execution resources MUST be created

### Requirement: One authoritative definition serves presentation and acceptance

The failure-retryability allowlist MUST be a single authoritative definition consumed identically at presentation time (deciding whether the failure notice renders the Retry action) and at click acceptance time. Presentation and acceptance MUST NOT maintain divergent copies of the classification.

#### Scenario: Presentation and acceptance agree

- **WHEN** a failure category is classified retryable at presentation time and the target facts are unchanged at click time
- **THEN** acceptance MUST classify the same category as retryable
- **AND** a category presentation classifies as not retryable MUST never be accepted at click time
