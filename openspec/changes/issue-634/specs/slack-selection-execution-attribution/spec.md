### Requirement: A selection starts the chosen Agent's work from the original message

An accepted selection SHALL start the chosen candidate Agent's work from the chooser claim's retained original-message facts, with the original sender as the initiator of record: the resulting execution records the original message's workspace, conversation, message identity, and thread anchor as its provenance, not the click or the chooser message. A root multi-Bot mention SHALL launch the chosen Connection's Session in that thread through the existing channel-root launch path (thread launch reservation, provider inbox route, attachment binding, admission); an ambiguous reply in a multi-bound thread SHALL dispatch a follow-up to the chosen Connection's bound Session. The same launch and admission services the CLI and Web use SHALL be invoked, with no Slack-only execution path.

#### Scenario: Choosing an Agent from a root multi-mention launches its Session in the thread

- **WHEN** an accepted selection resolves a root message that mentioned several Bots
- **THEN** the chosen Connection's Session is launched in that thread from the retained task text and attachments
- **AND** the execution's provenance records the original sender as initiator and the original message identity, not the click

#### Scenario: Choosing an Agent for an ambiguous multi-bound-thread reply dispatches a follow-up

- **WHEN** an accepted selection resolves an unmentioned reply in a thread bound to several Connections
- **THEN** a follow-up is dispatched to the chosen Connection's bound Session from the retained reply facts
- **AND** no new Session is created for that thread by the selection

#### Scenario: Retained attachments are bound to the execution

- **WHEN** the original ambiguous message carried attachments and its selection is accepted
- **THEN** those attachments are bound to the started execution through the existing attachment binding path

### Requirement: Exactly one execution is attributed to exactly one Connection

A resolved ambiguous message SHALL produce exactly one execution, owned by exactly the chosen Connection. No other mentioned or bound Connection SHALL create an AgentJob, AgentSession, SessionInput, or provider inbox entry for that message. Connections that lost the chooser race SHALL remain no-ops for the message even after the selection resolves to another Connection.

#### Scenario: Only the chosen Connection owns the execution

- **WHEN** a selection is accepted for one candidate of a multi-Bot message
- **THEN** exactly one AgentJob, Session, and SessionInput lineage exists for that message, attributed to the chosen Connection
- **AND** every other mentioned Connection holds no execution resources for it

### Requirement: The selection decision is durably persisted before dispatch with a pre-allocated execution identity

The accepted selection SHALL be durably recorded as a selection operation before any dispatch occurs, and the execution identity for the resulting work SHALL be pre-allocated when the decision is recorded. The persisted selection record SHALL be the single authority for whether and how work may start for that ambiguous message.

#### Scenario: A crash between decision and dispatch leaves the authority intact

- **WHEN** the Server records the accepted selection and then fails before or during dispatch
- **THEN** the persisted selection record with its pre-allocated execution identity remains the sole authority for recovery
- **AND** no dispatch path may start work for that message outside that record

### Requirement: At most one execution under concurrency, redelivery, failover, and lost responses

Concurrent clicks on the same or different candidates, repeated clicks by the same user, Slack interaction redelivery, adapter failover, and lost interaction responses SHALL resolve to one recorded selection and create at most one AgentJob, AgentSession, and SessionInput for the ambiguous message. A second or late click after the decision is recorded SHALL observe the recorded selection and return the decision view, never a second chooser and never a second execution.

#### Scenario: Concurrent clicks on different candidates resolve to one selection

- **WHEN** two users click different candidate choices at the same time
- **THEN** exactly one selection is recorded and only that candidate's work starts
- **AND** the other click observes the recorded decision instead of starting work

#### Scenario: An interaction redelivery does not duplicate execution

- **WHEN** Slack redelivers the same selection interaction after the first was processed
- **THEN** the redelivery resolves to the recorded selection and creates no additional AgentJob, Session, or SessionInput

#### Scenario: A lost interaction response is recovered without duplication

- **WHEN** the Server commits the selection but its interaction response is lost and the adapter or a failover replays the click
- **THEN** the replay returns the recorded selection's outcome
- **AND** no second execution is created

#### Scenario: A late clicker sees the decision, not a second chooser

- **WHEN** a user clicks a chooser choice after the selection was already decided
- **THEN** the chooser message reflects the accepted selection for that user
- **AND** no additional chooser is posted for the ambiguous message

### Requirement: Server restart resumes or settles committed selections without a second execution

After a Server restart, a selection operation that was committed but not completed SHALL be resumed to completion or terminally settled, using its pre-allocated execution identity, without creating a second execution. Recovery SHALL NOT depend on the original click's adapter lease and SHALL NOT re-run click-time authorization or change the chosen candidate. A committed selection SHALL never be silently orphaned and never executed twice.

#### Scenario: A restart between commit and dispatch resumes the selection

- **WHEN** the Server restarts after a selection was committed but before its work was dispatched
- **THEN** recovery resumes the selection using the pre-allocated execution identity
- **AND** the resulting execution is the one recorded by the selection, not a duplicate

#### Scenario: An unrecoverable committed selection settles terminally

- **WHEN** recovery determines a committed selection can no longer produce its execution
- **THEN** the selection is settled terminally with a visible outcome
- **AND** no execution resources are created for it after settlement

### Requirement: Finished selection records are cleaned up under bounded retention

Selection records that reached a finished or settled state SHALL be removed under the existing Slack redelivery / delivery-reconciliation retention window (the Slack event retention window the Server already uses), not under a new long-term audit retention. The retention rule SHALL NOT remove pending or in-progress selection operations.

#### Scenario: A finished selection record is reaped after retention

- **WHEN** a selection record has been finished longer than the existing retention window
- **THEN** the record is removed by cleanup

#### Scenario: A pending selection record is never reaped

- **WHEN** cleanup runs while a selection operation is still pending or in progress
- **THEN** that record is retained
