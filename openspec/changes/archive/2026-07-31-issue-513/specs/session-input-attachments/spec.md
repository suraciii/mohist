### Requirement: A SessionInput carries explicit attachments alongside or instead of text

An Agent SessionInput accepted at launch or as a follow-up SHALL carry zero or more explicit attachments in addition to optional text. An input that contains at least one attachment but no text SHALL be a valid input. Mohist SHALL NOT synthesize, inject, or imply a hidden prompt on the user's behalf merely because an input has attachments and no text.

#### Scenario: Launch with attachments and no text is accepted

- **WHEN** a client launches an Agent with one or more valid attachments and no prompt text
- **THEN** Mohist SHALL accept the input and create the AgentJob, AgentSession, first SessionInput (carrying the attachments), and first AgentTurn
- **AND** SHALL NOT fabricate a prompt

#### Scenario: Follow-up with only attachments is accepted

- **WHEN** a client submits a follow-up with one or more valid attachments and no text to an AgentSession
- **THEN** Mohist SHALL accept it as a SessionInput and assign it to a Turn
- **AND** SHALL NOT fabricate a prompt

#### Scenario: Text-only input remains valid

- **WHEN** a client submits an input with text and no attachments
- **THEN** Mohist SHALL accept the input exactly as a text-only input is accepted today

### Requirement: Input acceptance requires text or at least one usable attachment

The mandatory-input constraint SHALL be "at least one of non-empty text or a usable attachment", replacing the current rule that text is mandatory. An input with empty/whitespace text and no usable attachment SHALL be rejected before execution.

#### Scenario: Empty input with no attachments is rejected

- **WHEN** a client submits an input whose text is empty or whitespace and that has no attachments
- **THEN** Mohist SHALL reject the input before any execution begins

### Requirement: Each submitted attachment receives a definitive acceptance result

At acceptance, Mohist SHALL produce a definitive result for every attachment the client submitted: accepted, or rejected with a specific reason. The reasons SHALL at minimum distinguish not-found, not-readable, exceeds-size-limit, and unsupported-type. Both the accepted set and the rejected set SHALL be reported to the caller.

#### Scenario: Mixed valid and invalid attachments are reported individually

- **WHEN** a client submits two attachments where one is valid and one exceeds the size limit
- **THEN** Mohist SHALL report the valid one as accepted and the oversized one as rejected with the size-limit reason

#### Scenario: A missing or unreadable attachment is reported with its reason

- **WHEN** a client references an attachment id that was never uploaded, has already expired, or cannot be read
- **THEN** Mohist SHALL report that attachment as rejected with the specific reason (not-found, expired, or not-readable)

### Requirement: Rejected attachments are surfaced and never silently dropped

Mohist SHALL NOT silently drop an attachment that fails validation, and SHALL NOT execute a turn with a silently reduced attachment set while reporting overall success. A rejected attachment SHALL remain visible to the caller as unused and SHALL NOT be provided to the Agent as if it were accepted.

#### Scenario: A rejected attachment is not given to the Agent

- **WHEN** an input contains one accepted and one rejected attachment and the turn executes
- **THEN** only the accepted attachment SHALL reach the Agent
- **AND** the rejected attachment SHALL be reported to the caller as unused

#### Scenario: All-rejected attachments with no text rejects the whole input

- **WHEN** a client submits an input whose attachments are all rejected and that has no text
- **THEN** Mohist SHALL reject the input rather than execute a turn with no usable content

### Requirement: Accepted attachments are bound exclusively to the accepting input at acceptance

When Mohist accepts an attachment, it SHALL bind that attachment exclusively to the one SessionInput being accepted. An attachment id that is already bound to another SessionInput, Session, or prior submission SHALL be rejected for the new input rather than silently shared.

#### Scenario: An already-owned attachment id is rejected for a different input

- **WHEN** an attachment id has already been accepted and bound by one SessionInput, and a different input submission references the same id
- **THEN** Mohist SHALL reject that reference for the new input and SHALL NOT provide the attachment to both inputs
