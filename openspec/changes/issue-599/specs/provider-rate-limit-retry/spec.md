### Requirement: Provider throttling is recognized as retryable capacity pressure
The Pi and OpenCode execution paths SHALL recognize HTTP 429 and equivalent Provider rate-limit signals, including structured status, retry events, and documented rate-limit messages. A recognized throttling signal SHALL be handled as retryable capacity pressure and SHALL retain the canonical Provider identity and available throttling facts.

#### Scenario: OpenCode reports a rate-limit retry event
- **WHEN** OpenCode reports a retry event or response identified as HTTP 429 or an equivalent rate-limit signal
- **THEN** the OpenCode path SHALL classify it as Provider rate limiting
- **AND** SHALL retain its Provider identity and throttling facts
- **AND** SHALL NOT immediately return `turn-failed` solely because that signal occurred

#### Scenario: Pi reports a rate-limit retry event
- **WHEN** Pi reports an automatic retry event whose status or Provider message identifies HTTP 429 or an equivalent rate-limit signal
- **THEN** the Pi path SHALL classify it as Provider rate limiting
- **AND** SHALL retain its Provider identity and throttling facts
- **AND** SHALL NOT immediately return `turn-failed` solely because that signal occurred

### Requirement: Production adapters expose one Provider attempt
The Pi and OpenCode adapters SHALL expose a Mohist-owned single-attempt boundary to the rate-limit coordinator. The coordinator SHALL acquire/release Provider admission around each actual model transport call; an SDK/session operation that can replay or back off without returning control SHALL NOT be used as that boundary.

Pi SHALL disable SDK auto-retry with `AgentSession.setAutoRetryEnabled(false)` and SHALL wrap the `ModelRuntime` stream/streamSimple transport so tool-loop model calls are individually admitted. OpenCode SHALL use a pinned `ProviderAttemptExecutor.executeOne` implementation whose typed request sets `retry: false`; the pinned server SHALL enforce that field by performing one Provider attempt and returning control without internal replay or delay. The current OpenCode SDK `session.prompt()` surface without that typed field and server behavior SHALL be rejected at runtime startup with `provider-attempt-boundary-unsupported`; it SHALL NOT be used as a fallback. The deadline warning path SHALL NOT call `session.promptAsync()` from a coordinated turn, because it would create an unadmitted model request.

#### Scenario: SDK replay is disabled before a Pi turn
- **WHEN** a Pi session is prepared for a Provider-coordinated turn
- **THEN** the adapter SHALL disable SDK auto-retry
- **AND** each ModelRuntime transport call SHALL be visible to the coordinator as one attempt
- **AND** a rate-limit response SHALL NOT cause a hidden SDK replay while a lease is released or backoff is running

#### Scenario: OpenCode lacks a single-attempt capability
- **WHEN** the configured OpenCode server/SDK does not support the pinned single-attempt request
- **THEN** the OpenCode runtime SHALL fail readiness with `provider-attempt-boundary-unsupported`
- **AND** RunnerHost SHALL NOT admit OpenCode work through an opaque `session.prompt()` path
- **AND** the runtime SHALL NOT claim Provider concurrency or bounded-retry support for that configuration

#### Scenario: Pi lacks a single-attempt capability
- **WHEN** the installed Pi SDK cannot disable auto-retry or the adapter cannot wrap the ModelRuntime transport
- **THEN** the Pi runtime SHALL fail readiness with `provider-attempt-boundary-unsupported`
- **AND** RunnerHost SHALL NOT admit Pi work through an opaque `session.prompt()` path
- **AND** the runtime SHALL NOT claim Provider concurrency or bounded-retry support for that configuration

#### Scenario: Adapter status events do not create hidden attempts
- **WHEN** either adapter receives SDK retry/status events while a Provider attempt is running or backing off
- **THEN** those events MAY supply diagnostics for the current signal
- **AND** SHALL NOT cause another Provider request unless the Mohist coordinator acquires a fresh lease and invokes `executeOne`

#### Scenario: Deadline warning does not bypass Provider admission
- **WHEN** a coordinated turn reaches its deadline-warning point
- **THEN** the runtime SHALL emit the warning through a non-Provider session/runtime event
- **AND** SHALL NOT invoke `session.promptAsync()` or any other raw model request outside the coordinator

### Requirement: Retry timing honors Retry-After or configured fallback backoff
When a throttling response supplies a valid `Retry-After` value, the execution path SHALL wait at least that duration before the next attempt, subject to the remaining bounded rate-limit wait. When `Retry-After` is absent or unusable, the execution path SHALL use the configured rate-limit backoff policy. The execution path MUST NOT issue the next Provider request during the selected wait.

#### Scenario: Retry-After controls the next attempt
- **WHEN** a Provider returns a rate-limit response with `Retry-After: 12`
- **AND** at least 12 seconds remain in the bounded rate-limit wait
- **THEN** the execution path SHALL wait at least 12 seconds before the next attempt
- **AND** SHALL NOT replace that delay with a shorter fallback delay

#### Scenario: Fallback backoff is used without Retry-After
- **WHEN** a Provider returns an equivalent throttling signal without a usable `Retry-After` value
- **THEN** the execution path SHALL calculate the wait from the configured rate-limit backoff policy
- **AND** SHALL NOT busy-loop or immediately resend the request

#### Scenario: Retry-After exceeds the remaining wait
- **WHEN** the Provider-declared delay is longer than the remaining bounded rate-limit wait
- **THEN** the execution path SHALL stop waiting when the bound expires
- **AND** SHALL return `provider-rate-limited` without making another Provider request

### Requirement: Rate-limit retries have an independent retry budget
Time spent waiting after a rate-limit response and attempts caused by rate-limit responses SHALL NOT consume the normal consecutive Provider retry budget. Genuine non-rate-limit Provider failures SHALL continue to use the normal Provider error policy. A rate-limited sequence that recovers before its bounded wait expires SHALL complete successfully.

#### Scenario: Rate-limit retries recover after the ordinary threshold
- **WHEN** a Provider emits more rate-limit responses than the normal consecutive retry threshold
- **AND** the bounded rate-limit wait has not expired
- **AND** a later attempt succeeds
- **THEN** the execution path SHALL make the later attempt available
- **AND** SHALL report a successful turn
- **AND** SHALL NOT report `turn-failed` merely because the ordinary threshold was exceeded

#### Scenario: A genuine Provider failure keeps its existing classification
- **WHEN** a Provider failure is not HTTP 429 or an equivalent rate-limit signal
- **THEN** the execution path SHALL apply the normal Provider error policy
- **AND** SHALL preserve the existing genuine runtime or task failure classification when that policy is exhausted
- **AND** SHALL NOT classify the failure as `provider-rate-limited`

### Requirement: Rate-limit waiting is bounded and expires distinctly
The execution path SHALL bound the total waiting period caused by Provider throttling using configured rate-limit wait policy. If the bound expires before a successful Provider response, the execution path SHALL stop further rate-limit retries and return an outcome whose code or category is exactly `provider-rate-limited`, rather than `turn-failed`. The outcome SHALL include the canonical Provider identity, the latest throttling signal, and sufficient retry timing or wait-bound facts for an actionable later retry.

#### Scenario: Persistent throttling reaches the bound
- **WHEN** every Provider attempt remains rate limited until the configured wait bound is exhausted
- **THEN** the execution path SHALL stop issuing Provider requests
- **AND** SHALL return `provider-rate-limited`
- **AND** SHALL include the Provider identity, latest throttling information, and bounded wait information
- **AND** SHALL NOT return `turn-failed` as the sole classification

#### Scenario: Throttling recovers before the bound
- **WHEN** a later Provider attempt succeeds before the configured wait bound expires
- **THEN** the execution path SHALL return the normal successful turn result
- **AND** SHALL release all Provider admission held by the completed attempt
- **AND** SHALL NOT leave the turn in a rate-limited terminal state

### Requirement: Cancellation interrupts admission and rate-limit waits
The execution path SHALL honor cancellation while waiting for Provider admission and while waiting for rate-limit backoff. Cancellation SHALL prevent a subsequent Provider request, release any acquired Provider admission, and preserve the existing cancellation or interruption classification. Cancellation MUST NOT become `provider-rate-limited` merely because it occurred during a throttled sequence.

#### Scenario: Cancellation during rate-limit backoff
- **WHEN** a turn is waiting for `Retry-After` or configured rate-limit backoff
- **AND** its cancellation signal is raised
- **THEN** the execution path SHALL end the wait promptly
- **AND** SHALL NOT issue the next Provider attempt
- **AND** SHALL return the existing cancellation or interruption outcome

#### Scenario: Cancellation during Provider admission
- **WHEN** a retry attempt is waiting for its Provider admission permit
- **AND** its cancellation signal is raised
- **THEN** the execution path SHALL stop waiting promptly
- **AND** SHALL NOT issue that retry attempt
- **AND** SHALL NOT report the cancellation as bounded rate-limit expiry
