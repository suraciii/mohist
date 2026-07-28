### Requirement: Three-valued follow-up sync result

The follow-up call SHALL return exactly one of three sync results describing whether Mohist accepted the input: `accepted` (Mohist persisted the `SessionInput`), `rejected` (Mohist confirmed it did not accept the input), or `unknown` (Mohist could not confirm acceptance). The `accepted` / `rejected` / `unknown` distinction SHALL NOT be collapsed into a single `sent` value or a transport-level error code.

#### Scenario: Accepted sync result

- **WHEN** a follow-up is accepted and the `SessionInput` is persisted
- **THEN** the response SHALL indicate `accepted`

#### Scenario: Rejected sync result

- **WHEN** Mohist confirms it will not accept a follow-up (for example invalid input or a precondition that the Server denies)
- **THEN** the response SHALL indicate `rejected`
- **AND** SHALL NOT create a `SessionInput`

#### Scenario: Unknown sync result on uncertain acceptance

- **WHEN** the Server cannot confirm whether the follow-up was accepted (for example Runner or Runtime uncertainty)
- **THEN** the response SHALL indicate `unknown`
- **AND** SHALL direct the caller to reconcile using the same call identity rather than resend with a new identity

### Requirement: Stable input and turn identity in the response

An `accepted` follow-up response SHALL return the stable `SessionInput` Id and the `AgentTurn` Id the input was assigned to, so the caller can observe that input and turn on subsequent reads.

#### Scenario: Accepted response carries input and turn identity

- **WHEN** a follow-up is accepted
- **THEN** the response SHALL include the `SessionInput` Id
- **AND** SHALL include the `AgentTurn` Id the input was assigned to

#### Scenario: Idempotent retry returns the original identity

- **WHEN** a follow-up with idempotency key `K` is retried with the same `K` after the original was accepted
- **THEN** the response SHALL return the original `SessionInput` Id and `AgentTurn` Id
- **AND** SHALL NOT return a new identity

### Requirement: Client idempotency key transport

A follow-up request SHALL accept a client-provided idempotency key that identifies the call identity. The same key on retry SHALL resolve to the same `SessionInput`; a new key SHALL be treated as a new call. Both Web and CLI SHALL send the idempotency key when submitting a follow-up, including on retry after a lost response.

#### Scenario: Web sends an idempotency key and reuses it on retry

- **WHEN** a user submits a follow-up from Web and the response is lost, then the user retries
- **THEN** Web SHALL send the same idempotency key for the retry as for the original submission
- **AND** the Server SHALL return the original input rather than a second input

#### Scenario: CLI sends an idempotency key and reuses it on retry

- **WHEN** a user submits a follow-up from the CLI and the response is lost, then the user retries
- **THEN** the CLI SHALL send the same idempotency key for the retry as for the original submission
- **AND** the Server SHALL return the original input rather than a second input

### Requirement: Shared status interpretation across Web and CLI

Web and CLI SHALL present follow-up status using the same model: the accepted/rejected/unknown sync outcome, input acceptance, and turn status (`queued` / `executing` / terminal). A user SHALL see the same interpretation of a given follow-up from either client. The `Inputs`/`Turns` observation is a status/identity view; clients SHALL render follow-up message text from the transcript and status from the observation, so a follow-up is never displayed twice.

#### Scenario: Web and CLI render the same status for the same follow-up

- **WHEN** the same follow-up is observed from Web and from the CLI
- **THEN** both SHALL render the same accepted/rejected/unknown outcome
- **AND** both SHALL render the same input acceptance and turn status

#### Scenario: Both clients distinguish accepted-pending from executing

- **WHEN** a follow-up input is accepted but its turn is queued, not executing
- **THEN** both Web and CLI SHALL show the input as accepted and pending (not executing)
