### Requirement: Only files explicitly attached to the current Slack message become attachments

A Slack file SHALL become a SessionInput attachment only when it is explicitly attached to the current inbound Slack message and is readable by the Connection's Bot. Files that appear only in imported thread-history startup context, plain URLs, or cloud-drive links SHALL NOT become attachments as a result of this change; a plain URL SHALL remain message text whose access is decided by the Agent's configured Skills and Runtime permissions.

#### Scenario: A file attached to the current message becomes an attachment

- **WHEN** a Slack message that triggers an input carries one or more files explicitly attached to that message, and the Bot can read them
- **THEN** those files SHALL enter the input attachment boundary as attachment candidates for the accepting SessionInput

#### Scenario: A plain URL is not auto-fetched

- **WHEN** a Slack message contains a URL but no explicitly attached file
- **THEN** the URL SHALL remain part of the message text
- **AND** Mohist SHALL NOT fetch the URL or attach it as a file merely because it appeared in the message

#### Scenario: A thread-history-only file is not attached

- **WHEN** a file appears only inside imported thread-history startup context and not on the current inbound message
- **THEN** it SHALL NOT become an attachment of the accepting SessionInput as a result of this change

### Requirement: Files are accepted on every inbound Slack dispatch path

Explicitly attached, Bot-readable files SHALL be accepted as attachments on each of the four inbound Slack dispatch paths: a DM that launches a new AgentSession, a DM follow-up to the current session, a channel root `@Bot` mention that launches bound to a thread, and a thread reply follow-up into a bound AgentSession.

#### Scenario: A DM launch carries files

- **WHEN** a user sends a Slack DM with attached files that starts a new AgentSession
- **THEN** the accepted files SHALL be bound to the first SessionInput of that session

#### Scenario: A DM follow-up carries files

- **WHEN** a user sends a Slack DM with attached files to a conversation that has a current AgentSession
- **THEN** the accepted files SHALL be bound to the follow-up SessionInput

#### Scenario: A channel root mention launches with files

- **WHEN** a user mentions the Bot in a channel root message with attached files
- **THEN** the accepted files SHALL be bound to the launch SessionInput of the thread-bound AgentSession

#### Scenario: A thread reply follow-up carries files

- **WHEN** a user replies in a thread bound to an AgentSession with attached files
- **THEN** the accepted files SHALL be bound to that follow-up SessionInput

### Requirement: File content is fetched by the Server, not by the adapter

The `mohist-slack` adapter SHALL forward only user-visible file metadata (name, content type, size, and the Slack file reference needed to fetch it) extracted from the inbound Slack event. The adapter SHALL NOT download file content. The Server SHALL fetch file content itself using the Connection's decrypted Bot credentials, and SHALL write the bytes into the Mohist-owned attachment store before binding. Once a file has been accepted and bound, its readability SHALL NOT depend on the adapter being alive, on a Slack temporary download URL remaining valid, or on the Bot credentials being available at read time.

#### Scenario: The adapter forwards metadata only

- **WHEN** a Slack event carrying files arrives at the adapter
- **THEN** the normalized envelope handed to the Server SHALL contain each file's user-visible metadata and Slack file reference
- **AND** SHALL NOT contain the file's downloaded bytes

#### Scenario: The Server fetches content with the Bot credentials

- **WHEN** the Server binds a Slack file to a SessionInput
- **THEN** it SHALL have fetched the file content through a Server-side Slack file read using the Connection's Bot credentials
- **AND** SHALL have stored the bytes in the Mohist attachment store

#### Scenario: An accepted file remains readable after its Slack URL expires

- **WHEN** a Slack file has been accepted and bound, and later the Agent's turn reads it after Slack's temporary download URL has expired
- **THEN** the read SHALL succeed because the content resides in the Mohist attachment store
- **AND** SHALL NOT require a fresh Slack download URL or the Bot credentials at read time

### Requirement: Each Slack file receives a definitive, honest acceptance result

For every file explicitly attached to the current Slack message, Mohist SHALL produce a definitive acceptance result: accepted, or rejected with a concrete reason. The rejection reasons SHALL distinguish at minimum not-readable (including a file the Bot is not authorized to read or whose download fails), exceeds-size-limit, unsupported-type, and expired. Mohist SHALL NOT report a file as accepted when its content could not be read, and SHALL NOT wrap a partial success as total success.

#### Scenario: A file the Bot cannot read is rejected

- **WHEN** a Slack file is explicitly attached but the Bot is not authorized to read it or its content download fails
- **THEN** Mohist SHALL reject that file with a not-readable reason
- **AND** SHALL NOT deliver it to the Agent as if it were accepted

#### Scenario: An oversized file is rejected while a valid one is accepted

- **WHEN** a Slack message carries two files where one is within the size limit and one exceeds it
- **THEN** Mohist SHALL accept the valid file and reject the oversized one with the exceeds-size-limit reason
- **AND** SHALL report both results rather than a single overall success

#### Scenario: An unsupported type is rejected

- **WHEN** a Slack file's content type is not among the accepted input attachment types
- **THEN** Mohist SHALL reject that file with the unsupported-type reason

### Requirement: An attachment-only Slack message is a valid input

A Slack message whose only task content is one or more usable files (no text remains after removing the Bot mention) SHALL be accepted as a well-defined SessionInput and SHALL start normally. Mohist SHALL NOT fabricate, inject, or imply a hidden prompt for such an input. A Slack message with no usable text and no usable file SHALL be rejected before execution.

#### Scenario: Files with no text launch normally

- **WHEN** a user sends a Slack message that mentions the Bot, carries one or more usable files, and has no remaining text after the Bot mention is removed
- **THEN** Mohist SHALL accept the input, create the AgentJob, AgentSession, first SessionInput, and first AgentTurn
- **AND** SHALL NOT fabricate a prompt

#### Scenario: An empty mention with no file is not accepted

- **WHEN** a Slack message consists only of a Bot mention with no remaining text and no attached file
- **THEN** Mohist SHALL NOT create an AgentJob or SessionInput for it

### Requirement: Accepted Slack files record a Slack source and bind exclusively to one input

An attachment accepted from a Slack file SHALL record its provenance source as `slack`, distinguishing it from Web/CLI uploads, and the SessionInput observation SHALL expose that source alongside the file's name, content type, size, and availability. The accepted file SHALL bind exclusively to the one SessionInput that accepted it; another member, AgentSession, or Connection SHALL NOT read or reuse it by referencing its identifier.

#### Scenario: The Slack source is recorded and observable

- **WHEN** a Slack file has been accepted into a SessionInput
- **THEN** the SessionInput observation SHALL expose that attachment's source as `slack` with its name, content type, size, and availability

#### Scenario: A Slack file is not reused by another session

- **WHEN** a Slack file has been bound to a SessionInput in one AgentSession, and a different AgentSession attempts to read or bind the same file
- **THEN** Mohist SHALL deny the reuse because the file is already owned by the accepting input

### Requirement: Slack redelivery resolves to the same bound attachments

Slack transports messages at-least-once. Redelivery of the same Slack message identity SHALL resolve to the same SessionInput and the same set of bound attachments, without re-fetching file content that is already stored and without creating a duplicate SessionInput or duplicate attachment bindings. This SHALL hold across adapter and Server restarts.

#### Scenario: A redelivered message does not duplicate attachments

- **WHEN** Slack redelivers a message whose files have already been accepted and bound
- **THEN** Mohist SHALL resolve it to the existing SessionInput
- **AND** SHALL NOT fetch the file content again or create additional attachment bindings

#### Scenario: A restart does not lose pending file binding

- **WHEN** a Slack message carrying files has been durably accepted but the Server restarts before the files are fully bound
- **THEN** recovery SHALL complete the binding to the same SessionInput rather than dropping the files or creating a new input

### Requirement: The Bot reports the per-file result back to the Slack conversation

The Bot SHALL report, in the originating Slack conversation, the result for each file the user attached: which files the Agent received and which were unused, each with its concrete reason. A Slack user SHALL NOT be left believing the Agent read a file it did not.

#### Scenario: The user is told which files were used

- **WHEN** a Slack message with files is accepted and one or more files were rejected
- **THEN** the Bot SHALL post, in the same Slack conversation, an account of the accepted files and each rejected file with its reason

#### Scenario: The user is not falsely assured

- **WHEN** a Slack message's files were all rejected and there is no usable text
- **THEN** the Bot SHALL NOT claim the Agent is working on the files
- **AND** SHALL indicate the files were not used

### Requirement: Slack credentials, temporary addresses, and raw payloads stay inside the Server

The Connection's Bot credentials, Slack temporary download URLs, and the raw Slack event payload SHALL NOT enter the Agent Instructions, the Agent's reply, or the session transcript, and SHALL NOT be retained in the accepted attachment record. Only user-visible provenance metadata (name, content type, size, source) and the scoped content SHALL be persisted or exposed.

#### Scenario: The stored attachment record contains no Slack secrets

- **WHEN** a Slack file has been accepted and bound
- **THEN** the stored attachment record and its observation SHALL contain no Bot token, no Slack temporary download URL, no Slack file identifier, and no raw Slack event payload

#### Scenario: The transcript is free of leaked Slack transport detail

- **WHEN** a turn consumes a SessionInput that accepted Slack files
- **THEN** the resulting Instructions, reply, and transcript SHALL contain no Bot token, no Slack temporary download URL, and no raw Slack event payload introduced by the Slack file entry path
