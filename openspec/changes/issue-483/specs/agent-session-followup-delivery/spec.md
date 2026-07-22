### Requirement: Stable Follow-up operation identity
Mohist SHALL allocate a stable `operationId` before attempting every Follow-up delivery. For a Follow-up intended to start a new Turn, Mohist SHALL also allocate a stable `turnId` before delivery; retries, status reconciliation, and responses for that operation MUST reuse these identities.

#### Scenario: A new-Turn Follow-up delivery is retried for reconciliation
- **WHEN** an idle Session's Follow-up cannot be conclusively resolved on its first delivery attempt
- **THEN** subsequent reconciliation uses the original `operationId` and preallocated `turnId`

### Requirement: Admitted Follow-up records runtime acceptance
An admitted Follow-up SHALL record `followup.admitted` with `operationId`, `turnId`, `placement`, and admission time. `placement` MUST be either `current-turn` or `new-turn`; admission means only that the Runtime accepted the input and MUST NOT be interpreted as Turn completion.

#### Scenario: A running Turn receives a Follow-up
- **WHEN** the Runtime confirms acceptance of Follow-up input while a Turn is active
- **THEN** `followup.admitted` and `turn.input.added` are persisted together in that order for the current Turn

#### Scenario: An idle Session receives a Follow-up
- **WHEN** the Runtime confirms acceptance of Follow-up input while the Session is idle
- **THEN** `followup.admitted` and `turn.started` are persisted together in that order for the preallocated new Turn

### Requirement: Rejected Follow-up records no input fact
When Mohist confirms that a Runtime did not accept a Follow-up input, it SHALL record `followup.rejected` with `operationId`, rejection time, and a stable error code and diagnostic message. A rejected operation MUST NOT produce `turn.started` or `turn.input.added`.

#### Scenario: Runtime rejects an idle Follow-up
- **WHEN** a Runtime explicitly rejects a Follow-up that was intended to start a new Turn
- **THEN** the operation records `followup.rejected`, no new Turn is started, and the Session remains idle

### Requirement: Unconfirmed delivery preserves uncertainty
When Mohist cannot determine whether a Runtime accepted a Follow-up, it SHALL record `followup.delivery.unconfirmed` with `operationId`, `turnId`, attempted placement, observation time, and stable error information. An unconfirmed operation MUST NOT automatically resend its input or create a duplicate operation.

#### Scenario: Connection fails after sending a new-Turn Follow-up
- **WHEN** the Runtime connection fails after the input may have been accepted for an idle Session
- **THEN** the operation is delivery-unconfirmed, the preallocated Turn is projected as `currentTurn`, activity becomes `unknown`, and the input is not sent again automatically

#### Scenario: Reconciliation confirms an unconfirmed operation was not admitted
- **WHEN** reconciliation establishes that an unconfirmed new-Turn Follow-up was not accepted
- **THEN** the operation records `followup.rejected`, its candidate current Turn is cleared, and Session activity returns to `idle`

### Requirement: Follow-up operation convergence
For each `operationId`, `followup.admitted` and `followup.rejected` SHALL be mutually exclusive final admission results. An unconfirmed operation SHALL be reconcilable to one final admission result using the same `operationId`; a current-Turn unconfirmed operation MUST preserve the existing Turn's active state until independent runtime evidence changes it.

#### Scenario: Reconciliation confirms admission after uncertainty
- **WHEN** a delivery-unconfirmed Follow-up is later confirmed accepted by the Runtime
- **THEN** the same operation records `followup.admitted` and the corresponding Turn input fact without sending the input again

#### Scenario: An active Turn has unconfirmed appended input
- **WHEN** delivery of Follow-up input to the current Turn becomes unconfirmed
- **THEN** the current Turn remains active and is not ended or duplicated solely because the delivery result is unknown

### Requirement: Follow-up outcomes remain separate from Turn outcomes
Follow-up delivery events SHALL describe input admission only. Turn completion, failure, and stopping MUST be recorded exclusively by `turn.finished`, and Follow-up processing MUST NOT emit or require terminal Follow-up completion or failure events.

#### Scenario: An admitted Follow-up's Turn later completes
- **WHEN** an admitted Follow-up is processed by the Runtime and the containing Turn later completes
- **THEN** admission remains recorded by `followup.admitted` and completion is recorded once by `turn.finished`
