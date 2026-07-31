### Requirement: Users attach files at Agent launch and follow-up

The Web UI and the CLI SHALL each allow a user to attach one or more local files when launching an Agent and when submitting a follow-up, in addition to optional text. Both launch and follow-up SHALL support attachments.

#### Scenario: Web launch with attachments

- **WHEN** a user opens the Agent session composer, attaches files, optionally enters text, and launches
- **THEN** the Web UI SHALL submit the attachments as explicit input attachments alongside the text

#### Scenario: CLI launch with attachments

- **WHEN** a user runs the Agent launch command with file attachments and optionally a prompt
- **THEN** the CLI SHALL submit the attachments as explicit input attachments

#### Scenario: Web follow-up with attachments

- **WHEN** a user attaches files to a follow-up in the session view and sends
- **THEN** the Web UI SHALL submit the attachments as explicit input attachments

#### Scenario: CLI follow-up with attachments

- **WHEN** a user runs the follow-up command with file attachments
- **THEN** the CLI SHALL submit the attachments as explicit input attachments

### Requirement: Pending attachments are visible before submission

Before a user submits a launch or follow-up, the entry surface SHALL show the files queued to be sent, with at least their name and size, so the user can confirm what will be attached.

#### Scenario: Web shows pending attachments before send

- **WHEN** a user has attached files but not yet submitted in the Web composer
- **THEN** the composer SHALL list each pending file with its name and size

#### Scenario: CLI lists attachments before send

- **WHEN** a user specifies attachments on a launch or follow-up command
- **THEN** the CLI SHALL report the attachments it will submit

### Requirement: The entry shows the per-attachment acceptance result

After submission, the Web UI and the CLI SHALL show the result for each attachment — accepted, or rejected with its specific reason — so the user can see which files the Agent actually received and which were not used.

#### Scenario: Web shows mixed acceptance results

- **WHEN** a launch or follow-up is submitted with some accepted and some rejected attachments
- **THEN** the Web UI SHALL display each accepted attachment and each rejected attachment with its reason

#### Scenario: CLI reports per-attachment results

- **WHEN** a launch or follow-up is submitted with attachments
- **THEN** the CLI SHALL report which attachments were accepted and which were rejected, with reasons

### Requirement: Web attaches files as explicit input resources, not inline text references

The Web launch and follow-up composers SHALL attach files as explicit, owned input attachments. They SHALL NOT rely on embedding `att:` references inside the prompt text as the attachment mechanism.

#### Scenario: Web no longer relies on inline att references

- **WHEN** a user attaches a file in the Web launch composer and launches
- **THEN** the attachment SHALL be submitted as an explicit input attachment owned by the resulting SessionInput
- **AND** SHALL NOT be conveyed solely as an inline `att:` reference embedded in the prompt text
