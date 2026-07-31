### Requirement: An Agent input attachment is a Mohist-managed input resource

An attachment accepted into an Agent SessionInput SHALL become a Mohist-managed input resource. The owning SessionInput observation SHALL expose, for each accepted attachment, its user-visible provenance source, display name, content type, size, and availability.

#### Scenario: Accepted attachment metadata is observable

- **WHEN** an attachment has been accepted into a SessionInput
- **THEN** the SessionInput observation SHALL expose that attachment's name, content type, size, and source
- **AND** SHALL expose its availability as usable

### Requirement: Exclusive ownership by a single SessionInput

An accepted attachment SHALL be owned by exactly the one SessionInput that accepted it. Another SessionInput, AgentSession, user, or Agent Connection SHALL NOT read or reuse that attachment by referencing its id alone.

#### Scenario: Another session cannot read an accepted attachment

- **WHEN** a SessionInput in Session A has accepted an attachment, and Session B attempts to read that attachment's content by id
- **THEN** Mohist SHALL deny the read because the attachment is not owned by Session B

#### Scenario: A Connection cannot reuse an attachment by reference

- **WHEN** an Agent Connection submits an input referencing an attachment id already owned by another input
- **THEN** Mohist SHALL NOT provide that attachment to the Connection's turn

### Requirement: Content is readable only through the owning input's execution path

Attachment content SHALL be retrievable only by the execution path of the SessionInput that owns it. There SHALL be no unscoped route that returns an Agent input attachment's content by bare id.

#### Scenario: The owning turn can read content

- **WHEN** the Runner executes the turn that owns an accepted attachment
- **THEN** it SHALL be able to read that attachment's content through the owning input's scoped access path

#### Scenario: A bare-id content fetch is denied

- **WHEN** a caller requests an Agent input attachment's content by id without the owning session/input scope
- **THEN** Mohist SHALL NOT return the content

### Requirement: Unified retention and cleanup

Retention, expiry, and deletion of Agent input attachments SHALL follow Mohist's unified attachment rules, governed by ownership. A pending upload that was never bound to an accepted input SHALL expire on its pending TTL and be cleaned up; an attachment bound to an accepted SessionInput SHALL NOT be cleaned up merely because its pending TTL elapsed. Cleanup SHALL NOT delete or alter already-persisted session content, turns, replies, or work results.

#### Scenario: A bound attachment survives its pending TTL

- **WHEN** an attachment has been bound to an accepted SessionInput and its original pending TTL elapses
- **THEN** the attachment SHALL remain available and SHALL NOT be cleaned up as expired-pending

#### Scenario: A pending unbound upload expires

- **WHEN** an attachment was uploaded but never bound to any accepted input and its pending TTL elapses
- **THEN** Mohist SHALL clean it up

#### Scenario: Cleanup does not affect persisted work

- **WHEN** attachments expire or are cleaned up
- **THEN** already-persisted SessionInputs, AgentTurns, replies, and work results SHALL remain intact

### Requirement: The attachment resource does not expose caller secrets or raw platform events

The stored attachment resource and its observation SHALL NOT contain or expose the caller's temporary download addresses, provider tokens, or raw platform event payloads. Only the user-visible provenance metadata and the scoped content SHALL be exposed.

#### Scenario: Platform secrets are absent from the stored resource

- **WHEN** an attachment is ingested from a platform file that arrived with a temporary URL and an access token
- **THEN** the stored attachment record and its observation SHALL contain no temporary URL, no access token, and no raw platform event payload
