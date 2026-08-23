### Requirement: Authoritative retryable failure-category allowlist

The system SHALL define exactly one authoritative failure-category retryability allowlist, defined over the failure-category vocabulary the system actually records. A failed Turn's recorded failure category is retryable if, and only if, it is one of: `runner-unavailable`, `runner-lost`, `report-timeout` (the server-side reconciliation reasons), `deadline-exceeded`, `timeout` (the Pi-mapped form of `deadline-exceeded`), `generation-drain-timeout` (the deadline/timeout family), `unavailable-runtime`, `runtime-unavailable` (runtime unavailability at turn time and at dispatch preflight), or the reserved forward-looking tokens `rate-limited`, `probe-timeout`, `retry-safe` (no producer records them today; they classify as retryable the moment one does). Every other recorded category — including `invalid-input`, `permission-required`, `incompatible-runtime`, `incompatible-execution-configuration`, `unsupported_execution_configuration`, `missing-session`, `runtime-session-missing`, `conflict`, `interrupted`, `turn-failed`, `manager-credential-expired`, `workspace-unavailable`, `context_exhaustion`, `unknown`, and a generic turn failure — MUST be classified as not retryable.

#### Scenario: Server-recorded reconciliation category is in the allowlist

- **WHEN** a failed Turn's authoritative failure facts record a failure category of `runner-unavailable`, `runner-lost`, or `report-timeout`
- **THEN** the failed Turn MUST be classified as retryable

#### Scenario: Runner-recorded transient error kind is in the allowlist

- **WHEN** a failed Turn's authoritative failure facts record a failure category of `deadline-exceeded`, `timeout`, `generation-drain-timeout`, `unavailable-runtime`, or `runtime-unavailable` — the categories runner-reported turn failures actually carry
- **THEN** the failed Turn MUST be classified as retryable

#### Scenario: Reserved token is in the allowlist

- **WHEN** a failed Turn's authoritative failure facts record a failure category of `rate-limited`, `probe-timeout`, or `retry-safe`
- **THEN** the failed Turn MUST be classified as retryable

#### Scenario: Recorded category is a permanent failure

- **WHEN** a failed Turn's authoritative failure facts record a failure category of `invalid-input`, `permission-required`, `incompatible-runtime`, `missing-session`, `turn-failed`, `manager-credential-expired`, `workspace-unavailable`, `context_exhaustion`, or `unknown`
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

- **WHEN** a failed Turn records a permanent category such as `invalid-input` while its failure reason or error text mentions transient-sounding words like "unavailable", "timeout", or "retry"
- **THEN** the failed Turn MUST be classified as not retryable
- **AND** no retry affordance or retry acceptance MUST be granted on the basis of that text

#### Scenario: Retry-safe category with ordinary text

- **WHEN** a failed Turn records the failure category `retry-safe` regardless of how ordinary its error text reads
- **THEN** the failed Turn MUST be classified as retryable

### Requirement: Failed thread follow-up turns record a failure category

Terminal follow-up activity events for failed thread follow-up Turns MUST record the failing runtime's error kind as the failure category, applying the same error-kind-to-category mapping the AgentJob execution path applies, so that thread retryability is decided from recorded facts rather than permanently absent ones. A follow-up failure for which no runtime error kind is recoverable MAY record no failure category, and expired manager credentials keep recording `unknown`; such Turns are classified as not retryable.

#### Scenario: Runtime error kind is recorded as the category

- **WHEN** a thread follow-up Turn fails with a runtime error kind such as `unavailable-runtime` or `deadline-exceeded` from the OpenCode runtime, or `deadline-exceeded` from the Pi runtime (mapped to `timeout`)
- **THEN** the terminal follow-up event MUST record the mapped kind as the failure category
- **AND** the failed follow-up Turn MUST be classifiable as retryable from that recorded category alone

#### Scenario: Unknown failure records no category and is not retryable

- **WHEN** a thread follow-up Turn fails without a recoverable runtime error kind (for example an observer or transport failure carrying no error kind), or its terminal event comes from a runner that predates category reporting
- **THEN** the event MAY omit the failure category
- **AND** the Turn MUST be classified as not retryable

#### Scenario: Expired manager credentials keep recording unknown

- **WHEN** a thread follow-up Turn fails because its manager execution boundary expired
- **THEN** the terminal follow-up event MUST keep recording the failure category `unknown`
- **AND** the Turn MUST be classified as not retryable

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
