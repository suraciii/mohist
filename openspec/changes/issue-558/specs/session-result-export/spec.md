### Requirement: A Session's result exports with its execution context

The system SHALL provide a Session result export that carries the stable Session identity, the Turn identity of the exported execution, the Job identity when the execution is Job-bound, the Session's launch context references, and the public result facts: outcome status, result summary with failure reason when failed, start and end timing, model, and attributed cost. The export SHALL be understandable standalone: a reader with no access to Mohist can tell which execution it describes, what it was asked to do, and how it ended.

#### Scenario: Exporting a completed launch execution

- **WHEN** the user exports the result of a Session execution launched from an Agent with an Issue context
- **THEN** the export SHALL carry the Session identity, the Turn identity, the Job identity, the Issue context reference, the task summary, the completed outcome with result summary, timing, model, and attributed cost

#### Scenario: Exporting a follow-up turn

- **WHEN** the exported execution is a Follow-up Turn with no own AgentJob
- **THEN** the export SHALL carry the Session and Turn identities
- **AND** the Job identity SHALL be absent rather than inherited from an earlier Turn or fabricated

#### Scenario: Exporting without launch context

- **WHEN** the Session carried no launch context references
- **THEN** the export SHALL omit the context field rather than emitting an empty or defaulted context envelope

### Requirement: The export agrees with the history record and the Session page

The export SHALL describe the same execution as the Agent execution history record and the Session page timeline, using the same Session/Turn/Job identities, the same context reference envelope, the same outcome vocabulary, and the same failure interpretation. An unknown outcome SHALL export as unknown and MUST NOT be recorded as failed; Job result SHALL stay distinct from Session Activity in the export exactly as in the history projection.

#### Scenario: One execution reads the same everywhere

- **WHEN** the same execution is read as a history record, on the Session page, and from the export
- **THEN** all three SHALL present identical identities, context references, outcome status, result summary, and failure reason

#### Scenario: Unknown outcome exports honestly

- **WHEN** the exported execution's authoritative outcome is unknown
- **THEN** the export SHALL state unknown
- **AND** it MUST NOT record or imply a failure outcome for that execution

### Requirement: Export is available from the Web Session page and the CLI

The Web Session page SHALL offer an export action for the Session's interpreted result, and the CLI SHALL expose the same export as a `mo session` read command. Both surfaces SHALL produce the same export contract, and the CLI `--json` output SHALL carry the contract fields with absent facts omitted.

#### Scenario: Exporting from the Web Session page

- **WHEN** the user triggers the export action on a Session page
- **THEN** the produced export SHALL carry the full export contract for that execution
- **AND** its facts SHALL match what the page's timeline presents

#### Scenario: Exporting from the CLI

- **WHEN** the user runs the `mo session` export command for a Session
- **THEN** the output SHALL carry the same export contract as the Web export
- **AND** `--json` SHALL return the contract fields with absent facts omitted rather than nulled

### Requirement: Export is a read-only projection

The export SHALL be a read-only projection of existing result, usage, identity, and context facts. Producing an export MUST NOT mutate Session, Turn, or Job state, MUST NOT introduce new transcript facts, and MUST NOT trigger recovery, settlement, or other lifecycle behavior.

#### Scenario: Exporting has no side effects

- **WHEN** the user exports a Session's result while the Session is running or after it ended
- **THEN** the Session's, Turn's, and Job's authoritative state SHALL be unchanged by the export
- **AND** repeating the export SHALL produce the same facts for the same execution
