## Requirement: Retry the existing generation closeout obligation

`ClosingProcessGeneration` is the single durable obligation for a lost or replaced Runner process generation. It MUST be persisted before closeout scans and MUST remain persisted across activation until both Workflow and AgentJob scans complete without query, load, delivery, or outstanding-verdict failure. No per-owner pending ledger or second queue is allowed.

### Scenario: One owner failure does not stop another

- **WHEN** one matching owner delivery throws or returns `Outstanding` and another matching owner is available
- **THEN** the Runner MUST attempt the other owner independently, log the failed owner with Runner identity, and retain `ClosingProcessGeneration` for retry.

### Scenario: Definitive owner verdict retires on replay

- **WHEN** a generation-fenced owner method returns `Accepted` or `Refused`
- **THEN** the Runner MUST treat that owner attempt as definitive and MUST NOT retain the generation solely for that verdict. Repeating the same delivery MUST remain safe under the existing owner fence/idempotency rules.

### Scenario: Reminder retries a transient failure

- **WHEN** a closeout delivery fails on the first pass and a later `presence` reminder runs
- **THEN** the Runner MUST retry the same `ClosingProcessGeneration` and clear it only after all owner scans and deliveries are definitive.

### Scenario: Activation retries a transient failure

- **WHEN** the grain reactivates with `ClosingProcessGeneration` persisted
- **THEN** activation MUST retry that generation before reopening a replacement generation or removing the reminder.

### Scenario: Checks closeout keeps its generation fence

- **WHEN** the lost generation owns running checks
- **THEN** the Runner MUST use the existing generation-fenced Workflow failure method with the checks work identity and MUST not confuse a checks identity with an Agent task identity.

### Scenario: Presence reminder remains armed

- **WHEN** a Runner is offline but `ClosingProcessGeneration` is non-empty
- **THEN** the `presence` reminder MUST remain registered at the existing ten-second retry cadence.

## Requirement: Owner authority remains unchanged

Workflow and AgentJob owners MUST remain responsible for active-work validation, generation fences, terminalization, and idempotency. `Accepted` and `Refused` are definitive completion; `Outstanding`, unavailable state, query/load failure, and exceptions retain the Runner obligation. The Runner MUST NOT add owner deadlines, interruption/Unknown APIs, generic messaging, or Runner-process durable journals.
