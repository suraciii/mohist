### Requirement: Accepted attachments reach the Runtime as readable content

When a turn executes, every attachment accepted by the SessionInputs that turn consumes SHALL be delivered to the Runtime as content the Agent can read within that turn. The Runner SHALL resolve accepted attachment content before or during the turn, rather than passing the Agent an opaque reference it cannot open.

#### Scenario: The Runtime receives readable content for an accepted attachment

- **WHEN** a turn consumes a SessionInput that accepted an attachment, and the turn executes
- **THEN** the Runtime SHALL receive the attachment's content in a form the Agent can read
- **AND** SHALL NOT receive only an unresolvable text or URL reference

#### Scenario: An attachment-only turn delivers content without a fabricated prompt

- **WHEN** a turn consumes an input that has attachments but no text
- **THEN** the turn SHALL execute with the attachment content delivered to the Agent
- **AND** Mohist SHALL NOT fabricate a prompt to make the turn valid

### Requirement: Delivery resolves content through the owning input's scoped path

The Runner SHALL resolve attachment content only through the access path scoped to the owning SessionInput and its turn. It SHALL NOT require or use caller-supplied temporary download addresses or credentials to obtain the content.

#### Scenario: Delivery uses scoped access, not caller URLs

- **WHEN** the Runner resolves an accepted attachment for a turn
- **THEN** it SHALL obtain the content via the owning input's scoped path
- **AND** SHALL NOT use any temporary download URL or provider token supplied by the caller

### Requirement: Delivery does not leak secrets or raw events into the turn

As a result of attachment delivery, temporary download addresses, provider tokens, and raw platform event payloads SHALL NOT enter the Agent instructions, the Agent's reply, or the session transcript.

#### Scenario: The transcript contains no leaked secrets

- **WHEN** an attachment is delivered to a turn
- **THEN** the resulting instructions, reply, and transcript SHALL contain no temporary URL, no provider token, and no raw platform event payload introduced by the delivery
