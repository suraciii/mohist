### Requirement: The first viewport surfaces the most recent result

The Session view SHALL surface the session's most recent result in the first viewport: the latest Turn's outcome together with its result or failure evidence, presented as a signal distinct from the Activity badge. The most-recent result SHALL use result vocabulary — `completed` with its result message, `failed` with failure category and reason, `cancelled`, or `unresolved` — and SHALL NOT be expressed through Activity vocabulary.

#### Scenario: Completed latest Turn explains what the execution produced

- **WHEN** the session's latest Turn completed with a result message
- **THEN** the first viewport SHALL present the most recent result as completed together with the result message or a bounded excerpt of it

#### Scenario: Failed latest Turn explains why

- **WHEN** the session's latest Turn failed
- **THEN** the first viewport SHALL present the most recent result as failed together with the recorded failure category and failure reason

#### Scenario: No terminal Turn stays honestly unresolved

- **WHEN** the session has no terminal Turn
- **THEN** the first viewport SHALL present the most recent result as unresolved rather than implying success or failure

#### Scenario: The result is distinct from the Activity badge

- **WHEN** the Session header shows both the Activity badge and the most recent result
- **THEN** the two SHALL be presented as distinct signals
- **AND** the most-recent result MUST NOT be derived from the Activity value

### Requirement: The launch result stays distinct from later Turn results

For sessions created by an AgentJob launch — direct `agent-launch` sessions and Slack `agent-connection` sessions alike, both launch-coordinator creations whose first AgentTurn carries the JobId — the Session view SHALL present the first AgentJob result, supplied by that first AgentTurn, as the launch result, distinct from later Turn results, reusing the existing launch-observation read surface. Later Turns MUST NOT rewrite the presented launch result, and a launch result MUST NOT be fabricated for a session none of whose Turns carries a JobId.

#### Scenario: Launch result is presented for launch-origin sessions

- **WHEN** the session was created by an AgentJob launch whose first AgentTurn has a terminal result
- **THEN** the Session view SHALL present that result as the launch result

#### Scenario: Agent-connection sessions present their launch result

- **WHEN** a Slack agent-connection session — created by the launch coordinator with a real AgentJob, so its first AgentTurn carries that JobId — has a first AgentTurn with a terminal result
- **THEN** the Session view SHALL present that result as the launch result, exactly as for direct launch sessions

#### Scenario: A follow-up Turn does not rewrite the launch result

- **WHEN** a follow-up Turn reaches a terminal result after the first Turn
- **THEN** the launch result SHALL remain the first AgentTurn's result
- **AND** the follow-up's result SHALL be presented as a later Turn result

#### Scenario: Sessions without a JobId-bearing Turn do not fabricate a launch result

- **WHEN** none of the session's Turns carries a JobId — the session was not created by an AgentJob launch
- **THEN** the Session view SHALL NOT present a launch result

### Requirement: Terminal Turn results are first-class outcome entries

The Session timeline SHALL present each terminal Turn result as a first-class outcome entry with a sentence-form summary for `completed`, `failed`, `cancelled`, and unresolved Turn outcomes. A completed Turn result MUST NOT render as a muted one-line status row. A failed Turn outcome SHALL remain a prominent error entry that never collapses into a group.

#### Scenario: Completed Turn results read as outcome sentences

- **WHEN** a Turn completes with a result
- **THEN** the timeline SHALL render a sentence-form outcome entry that carries the result summary
- **AND** the entry MUST NOT render as a muted status line

#### Scenario: Failed Turn results stay prominent

- **WHEN** a Turn fails
- **THEN** the timeline SHALL render a prominent error outcome entry carrying the failure category and reason
- **AND** the entry MUST NOT collapse into a grouped summary of routine activity

#### Scenario: Cancelled Turn results are stated

- **WHEN** a Turn is cancelled
- **THEN** the timeline SHALL render a cancelled outcome entry in sentence form

#### Scenario: Unresolved Turn outcomes are stated honestly

- **WHEN** a Turn ends without a confirmable result
- **THEN** the timeline SHALL render an unresolved outcome entry
- **AND** it MUST NOT present that Turn as completed or failed

### Requirement: Outcome entries expose expandable structured result evidence

Each terminal Turn outcome entry SHALL provide expandable structured result evidence layered on the same already-recorded facts the raw view exposes: the result message, an output excerpt, the failure category and reason, the exit code when recorded, and the inputs the Turn processed.

#### Scenario: Expanding a completed outcome

- **WHEN** the user expands a completed Turn's outcome entry
- **THEN** the evidence SHALL show the result message, an output excerpt when recorded, and the inputs the Turn processed

#### Scenario: Expanding a failed outcome

- **WHEN** the user expands a failed Turn's outcome entry
- **THEN** the evidence SHALL show the failure category and failure reason, and the exit code when recorded

#### Scenario: Evidence matches the raw view

- **WHEN** the expanded evidence is compared with the raw event view for the same Turn
- **THEN** both SHALL be presentations of the same underlying recorded facts
- **AND** no separate result record SHALL be introduced for the presentation

### Requirement: Result-entry semantics are documented

`design/session-timeline.md` SHALL define the result-entry semantics for terminal Turn outcomes in the timeline, and the AgentSession implementation-gap note in `docs/web-ui.md` SHALL no longer list the result-presentation gaps this change removes.

#### Scenario: The timeline design defines result entries

- **WHEN** `design/session-timeline.md` is read after this change
- **THEN** it SHALL define how terminal Turn results are presented as outcome entries with structured expandable evidence

#### Scenario: Removed gaps no longer appear as gaps

- **WHEN** the AgentSession implementation gaps in `docs/web-ui.md` are read after this change
- **THEN** the first-viewport most-recent result and the terminal Turn result-entry presentation MUST NOT be listed as missing
