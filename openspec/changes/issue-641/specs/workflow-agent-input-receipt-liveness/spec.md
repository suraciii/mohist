### Requirement: Ordinary Workflow input receipt waiting has one finite live-process budget

For an ordinary Workflow Agent turn using OpenCode or Pi, the Runner MUST enqueue the turn-opening `session.input` into the current Runtime Event Queue before Runtime invocation and MUST wait for its receipt under one finite task-level budget. The budget MUST cover retryable transport failures, delivery timeouts, empty responses, non-matching responses, and queue retry delays without resetting or extending. While the budget remains, retryable outcomes MUST retain the current queue record for its existing retry path.

The queue record and evidence are process-volatile under the current master contract. This requirement does not imply restart persistence.

#### Scenario: Retryable receipt never recovers

- **WHEN** every `session.input` delivery outcome remains retryable through the task-level budget
- **THEN** the receipt wait MUST terminate when the budget is exhausted
- **AND** the Agent Runtime MUST NOT be invoked
- **AND** another retry MUST NOT extend the owning task wait past the exhausted budget

#### Scenario: Retryable receipt recovers before the deadline

- **WHEN** retryable outcomes occur and a receipt matching the input delivery id, expected AgentSession id, and non-empty AgentTurn id arrives before the deadline
- **THEN** the Runner MUST accept that receipt
- **AND** it MUST invoke the selected Agent Runtime exactly once using the acknowledged AgentTurn identity

### Requirement: Receipt-budget exhaustion is actionable

When the receipt budget expires before matching acceptance or an existing definitive outcome, the Workflow task MUST finish with failure code `session-reporting-failed`. Its message MUST contain the input record id, latest structured reason, elapsed wait, exhausted budget, delivery attempts, and retries. OpenCode and Pi MUST expose equivalent classification and evidence.

#### Scenario: Structured retry reason is preserved

- **WHEN** the queue observes structured retryable delivery failures and the receipt budget expires
- **THEN** the terminal message MUST contain the latest structured reason
- **AND** it MUST report elapsed wait, budget, attempts, and retries

#### Scenario: Cancellation interrupts receipt waiting

- **WHEN** the task `AbortSignal` is aborted while the bounded input receipt wait is pending
- **THEN** the wait MUST reject promptly with a cancellation classification
- **AND** the action boundary MUST return `session-reporting-failed`
- **AND** the Agent Runtime MUST NOT be invoked

### Requirement: Ending a waiter does not mutate other queue facts

Timeout or cancellation MUST remove only the local receipt waiter and its volatile evidence. It MUST NOT remove, rewrite, reorder, or synthesize the original queued input or unrelated queue records. Server facts already accepted before the local outcome remain outside this local boundary and are not changed.

#### Scenario: Budget expires with other records pending

- **WHEN** the task-level receipt budget expires while the input and unrelated Runtime Event Queue records remain pending
- **THEN** both records MUST retain their prior identity and content in the current process
- **AND** the queue MUST remain eligible to apply its existing retry and delivery rules
- **AND** the timeout MUST NOT synthesize a receipt or terminal delivery fact

#### Scenario: Matching receipt arrives after task failure

- **WHEN** the task has finished because its receipt budget expired and the retained input later receives a matching receipt
- **THEN** the queue MAY settle that record under its normal acknowledgement rules
- **AND** the terminal Workflow task MUST NOT be reopened
- **AND** the Agent Runtime MUST NOT be invoked for that task

### Requirement: Existing definitive receipt semantics remain unchanged

A matching `session.input` receipt MUST still require the submitted input delivery id, expected AgentSession id, and non-empty AgentTurn id for Workflow records. A permanent refusal or already-consumed outcome MUST remain `AlreadyConsumedRuntimeEventError` and MUST NOT become receipt-budget exhaustion. A non-matching positive response MUST remain retryable while the original budget remains.

#### Scenario: Immediate matching acceptance

- **WHEN** the first delivery returns a matching receipt
- **THEN** the queue MUST retire the record and resolve the waiter without waiting for the budget
- **AND** the Runner MUST invoke the Runtime exactly once

#### Scenario: Already-consumed outcome

- **WHEN** the current queue reaches its existing already-consumed outcome before the deadline
- **THEN** the receipt wait MUST end with `already-consumed`
- **AND** the Runner MUST NOT invent an AgentTurn or invoke the Runtime
- **AND** the result MUST NOT be reported as receipt-budget exhaustion
