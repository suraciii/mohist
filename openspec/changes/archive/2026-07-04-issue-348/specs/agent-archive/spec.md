### Requirement: Archive flips agent status to archived

Archiving an agent SHALL set the agent's `Status` to `archived`, advance `UpdatedAt` to the current time, and persist the change. The archived agent SHALL leave the Active group of the agent list.

#### Scenario: Archive transitions an active agent to archived
- **WHEN** an active agent is archived
- **THEN** the agent's `Status` SHALL become `archived`
- **AND** the agent's `UpdatedAt` SHALL advance to the time of archive
- **AND** the persisted state SHALL reflect the archived status

#### Scenario: Archived agent leaves the Active list group
- **WHEN** an agent that was in the Active group is archived
- **THEN** the agent SHALL no longer appear in the Active group of the agent list

### Requirement: Archived agents are visible in a distinct Archived list group

The agent list query SHALL include archived agents end-to-end so the existing "Archived (n)" list section is populated. Archived rows SHALL render distinctly from active rows and SHALL remain navigable into the agent detail page. The agent list client call SHALL request archived rows (e.g. by passing `all: true`), surfacing the already-shipped but currently dormant Archived section.

#### Scenario: Archived agents appear in the Archived section
- **WHEN** the agent list is rendered and at least one archived agent exists in the project
- **THEN** the "Archived (n)" section SHALL list each archived agent
- **AND** the count `n` SHALL equal the number of archived agents

#### Scenario: Archived rows are visually distinct but navigable
- **WHEN** an archived agent row renders in the list
- **THEN** the row SHALL be visually distinguished from active rows (e.g. reduced opacity and an Archived badge)
- **AND** activating the row SHALL navigate to that agent's detail page

#### Scenario: Empty Archived section is omitted
- **WHEN** the agent list is rendered and no archived agents exist
- **THEN** the "Archived" section SHALL NOT be rendered

### Requirement: Archived agents cannot start new sessions

An archived agent SHALL NOT be launchable for new sessions. The agent detail page "New Session" control SHALL be disabled for an archived agent.

#### Scenario: New Session is disabled for an archived agent
- **WHEN** the detail page renders for an archived agent
- **THEN** the "New Session" control SHALL be disabled

### Requirement: The archive confirmation dialog describes the real effect

The archive confirmation dialog text SHALL accurately describe what archive does: the agent leaves the Active group and cannot start new sessions. Because this change ships a working unarchive path, the dialog MAY honestly state the action is reversible from the agent detail page. The dialog SHALL NOT retain the pre-fix phrases that promise behavior the system does not deliver (the vague "remain visible" claim and the unsupported "can be reversed" promise).

#### Scenario: Confirmation text matches archive's actual effect
- **WHEN** the archive confirmation dialog is shown
- **THEN** the description SHALL state that the agent leaves the Active group and cannot start new sessions
- **AND** SHALL NOT contain the pre-fix "remain visible" / "can be reversed" phrasing unbacked by the product

#### Scenario: Reversibility claim is backed by a working affordance
- **WHEN** the archive confirmation dialog states the action is reversible
- **THEN** a working unarchive affordance SHALL be reachable from the agent detail page

### Requirement: The detail-page Actions control behaves as its label promises

The agent detail page "Actions" card SHALL NOT present a button whose label promises a direct archive action while its behavior only opens the Edit dialog. Either the Actions "Archive" button SHALL trigger archive directly (with its own confirmation step), or the redundant Actions archive button SHALL be removed so archiving is entered solely via the Edit dialog. The top-level Edit button SHALL remain the canonical edit entry.

#### Scenario: Actions Archive button triggers archive directly
- **WHEN** the detail-page Actions "Archive" button is activated for an active agent
- **THEN** it SHALL initiate the archive flow (confirmation then archive) directly
- **AND** SHALL NOT merely open the Edit dialog

#### Scenario: No label/behavior mismatch remains
- **WHEN** the detail page renders the Actions card for an active agent
- **THEN** no control labeled "Archive" SHALL have the sole side effect of opening the Edit dialog

### Requirement: Archiving an unknown agent is a not-found

Archiving an agent that does not exist SHALL return a not-found result, matching the existing archive path contract.

#### Scenario: Archive of a non-existent agent
- **WHEN** archive is requested for an agent id that does not exist
- **THEN** the operation SHALL return a not-found result
- **AND** SHALL NOT create or mutate any agent
