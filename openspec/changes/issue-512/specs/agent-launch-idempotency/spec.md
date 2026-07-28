### Requirement: Launch carries a stable client call identity

Every Web and CLI Agent launch SHALL carry a non-empty client call identity that remains unchanged for every retry of the same launch intent. A new launch intent SHALL use a new call identity; a caller MUST NOT create a new identity merely because the prior response was lost, timed out, or its connection ended.

#### Scenario: Web retries after a lost response
- **WHEN** Web submits a launch and does not receive its response before the connection fails
- **THEN** Web retries with the original call identity and does not initiate a second launch intent

#### Scenario: CLI retries after interruption
- **WHEN** `mo agent launch` is interrupted after sending a launch request
- **THEN** a retry of that invocation uses the original call identity and not a newly generated identity

### Requirement: A call identity has one launch outcome

For one Project and launch call identity, Mohist SHALL create or return exactly one AgentJob, exactly one AgentSession, exactly one first SessionInput, and exactly one first AgentTurn. All successful retries of that identity MUST return the same four stable identities and MUST NOT create another dispatch, job, session, input, or turn.

#### Scenario: Duplicate request reaches the server
- **WHEN** two otherwise valid launch requests for the same Project carry the same call identity
- **THEN** both requests resolve to the same AgentJob, AgentSession, SessionInput, and AgentTurn

#### Scenario: Distinct launch intents
- **WHEN** a caller starts two valid launches with different call identities
- **THEN** Mohist creates distinct AgentJob and AgentSession resources for the two intents

### Requirement: Reused call identity preserves its original intent

Once Mohist has accepted a launch call identity, its Agent, prompt, context, and resolved execution definition SHALL remain the canonical intent for that identity. A later request that reuses the identity with different launch content MUST be rejected as a conflict and MUST NOT modify or replace the original resources.

#### Scenario: Retry changes the prompt
- **WHEN** a retry uses an already accepted call identity but supplies a different prompt
- **THEN** Mohist rejects the retry as a conflicting reuse and leaves the original launch unchanged

#### Scenario: Retry follows an Agent or context change
- **WHEN** a caller retries an accepted launch with the same request and call identity after its Agent is archived or renamed, or a referenced context is changed or removed
- **THEN** Mohist returns or resumes the original launch from its canonical snapshot without revalidating the changed Agent, context, or execution configuration

### Requirement: Accepted launch records the first input and turn durably

Mohist SHALL not report a launch as accepted until the AgentJob, AgentSession, first SessionInput, and first AgentTurn are durably linked to the same launch intent. The first SessionInput MUST retain its stable identity and accepted content, and the first AgentTurn MUST retain its stable identity and association with that input and AgentJob across Server restart, Runner restart, queueing, and client disconnection.

#### Scenario: Server restarts after launch acceptance
- **WHEN** Mohist restarts after reporting a launch as accepted but before the Runner completes the first turn
- **THEN** the original Job, Session, Input, and Turn remain readable and the accepted input is neither removed nor replaced

#### Scenario: Queue delays execution
- **WHEN** a launch input is accepted while no execution slot is immediately available
- **THEN** the original Input and Turn remain the pending work and no replacement input or turn is created

### Requirement: Invalid launch does not consume a call identity

A new launch identity with invalid input, an unresolved Agent or context reference, or an archived Agent MUST be rejected before any launch resources are created. Such rejection MUST NOT create an AgentJob, AgentSession, SessionInput, or AgentTurn for the supplied call identity.

#### Scenario: Whitespace prompt is rejected
- **WHEN** a caller submits a launch with a whitespace-only prompt
- **THEN** Mohist reports validation failure and creates none of the four launch resources
