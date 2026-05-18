## MODIFIED Requirements

### Requirement: Session surfaces show shared transcripts without collapsing task progress

Session timeline and transcript surfaces SHALL display a shared Plan agent session as one coherent transcript containing separate Mohist prompt blocks for the artifact tasks that executed in that session. Issue task progress surfaces SHALL continue to show each Plan artifact task as an independent task row.

#### Scenario: Shared Plan transcript contains multiple prompt blocks
- **WHEN** the user opens the session surface for a Plan run whose artifact tasks used `agentSessionRef: "plan-artifacts"`
- **THEN** the session surface SHALL show one Plan transcript for the real shared session
- **AND** that transcript SHALL include separate Mohist prompt blocks for the individual artifact tasks that executed in it

#### Scenario: Task list remains independent
- **WHEN** the user views the issue detail task progress for the same Plan run
- **THEN** the task list SHALL still show separate rows for `proposal`, `specs`, `design`, `tasks`, and `self-review`
- **AND** task completion SHALL NOT be inferred from transcript completion

#### Scenario: Build and Check session display is unchanged by default
- **WHEN** Build or Check tasks do not explicitly configure `agentSessionRef`
- **THEN** their session/transcript display SHALL remain task-local as before this change
