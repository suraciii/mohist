### Requirement: Agent-scoped session list read model carries result facts

The Agent-scoped session list read and the agent-filtered unified session list read SHALL expose, for every returned session, a first-input subject excerpt, the terminal Turn result facts of the session's latest AgentTurn, the session's accumulated cost when recorded, and its derived end time. The latest-Turn outcome SHALL use the result vocabulary `completed` (with the Turn's result message), `failed` (with failure category and reason when recorded), `cancelled`, or `unresolved`. These additions SHALL be read-only projections over facts that are already recorded — the change MUST NOT introduce new persisted session state, new events, or new transcript fact types — and the first AgentJob result SHALL be sourced from the existing AgentJob read surface and launch observation rather than a duplicated read path.

#### Scenario: Completed latest Turn carries its result message

- **WHEN** a listed session's latest AgentTurn has terminal status `completed` with a recorded result
- **THEN** the list item SHALL report outcome `completed` together with that Turn's result message

#### Scenario: Failed latest Turn carries failure evidence

- **WHEN** a listed session's latest AgentTurn has terminal status `failed`
- **THEN** the list item SHALL report outcome `failed` together with the recorded failure category and failure reason when present

#### Scenario: Cancelled latest Turn is reported as cancelled

- **WHEN** a listed session's latest AgentTurn has terminal status `cancelled`
- **THEN** the list item SHALL report outcome `cancelled`

#### Scenario: No terminal Turn resolves to unresolved

- **WHEN** a listed session has no terminal AgentTurn — no Turn, a queued or executing Turn, or a Turn whose status is `unknown`
- **THEN** the list item SHALL report outcome `unresolved`
- **AND** it MUST NOT infer `completed` or `failed` from the session's Activity

#### Scenario: First-input subject excerpt is bounded and honest

- **WHEN** a listed session has a recorded first input with text
- **THEN** the list item SHALL carry a bounded excerpt of that text as the session subject
- **AND** a session with no recorded first-input text SHALL leave the subject explicitly absent rather than fabricating one

#### Scenario: Session cost is carried when recorded

- **WHEN** a listed session's status snapshot records an accumulated usage cost
- **THEN** the list item SHALL carry the cost amount together with its currency
- **AND** a session with no recorded cost SHALL leave the cost explicitly absent rather than fabricating zero

#### Scenario: End time derives from recorded lifecycle anchors

- **WHEN** a listed session has recorded lifecycle anchors beyond creation
- **THEN** the list item SHALL carry an end time derived as the latest recorded anchor
- **AND** a session with no anchor beyond creation SHALL leave the end time explicitly absent rather than inferring one

### Requirement: History rows identify the task and its context

Each session row in the Agent page history SHALL identify the task it executed: the first-input subject excerpt as the row's primary subject, the session's recorded origin, its context references (Issue, Epic, repository, workspace, and Slack provenance when present), and its created and last-activity timestamps. The row's current Activity SHALL be presented as its own separate signal alongside these facts.

#### Scenario: Subject excerpt replaces the redundant Agent name

- **WHEN** a history row is rendered for a session with a first-input subject excerpt
- **THEN** the row's primary label SHALL be the subject excerpt
- **AND** the Agent name MUST NOT be the row's primary label, because every row in the history already belongs to that Agent

#### Scenario: Context references appear as references

- **WHEN** the session carries Issue, Epic, repository, or workspace context references
- **THEN** the row SHALL present each recorded reference, with Issue and Epic numbers resolving to their pages

#### Scenario: Absent context is omitted

- **WHEN** the session carries no context references
- **THEN** the row SHALL omit the reference presentation instead of rendering empty placeholders

#### Scenario: Timestamps and Activity are separate signals

- **WHEN** a history row is rendered
- **THEN** the row SHALL show the created and last-activity timestamps and the current Activity as a signal separate from the execution outcome

### Requirement: History rows surface duration and cost

Each session row in the Agent page history SHALL present the session's start time, its end time and elapsed duration, and its accumulated cost when recorded. A still-active session's elapsed SHALL be presented as time-so-far rather than a fabricated end time, and absent cost SHALL be omitted rather than rendered as zero.

#### Scenario: Ended sessions show start, end, and elapsed

- **WHEN** a history row is rendered for a session with a derived end time
- **THEN** the row SHALL show the start time, the end time, and the elapsed duration between them

#### Scenario: Active sessions show elapsed so far

- **WHEN** a history row is rendered for a session whose Activity is active
- **THEN** the row SHALL present the elapsed time so far
- **AND** it MUST NOT present a fabricated end time

#### Scenario: Cost is shown when recorded and omitted when not

- **WHEN** a session records an accumulated cost
- **THEN** the row SHALL show the cost amount with its currency
- **AND** a session with no recorded cost SHALL omit the cost signal rather than showing zero

### Requirement: Outcomes use result vocabulary with honest unknown handling

The Agent page history SHALL present each session's execution outcomes — the first AgentJob result and the latest Turn outcome — using result vocabulary derived from Turn result facts, never from Activity. History grouping SHALL be outcome-based. A session whose Activity is `unknown` MUST NOT be labeled or grouped as "Failed", and a failed AgentJob MUST NOT be presented as a failed AgentSession.

#### Scenario: Unknown Activity is never Failed

- **WHEN** a session's Activity is `unknown`
- **THEN** the history MUST NOT place that session under a "Failed" group
- **AND** the session's grouping SHALL be derived from its execution outcome, with the unknown Activity shown as its own signal

#### Scenario: A failed Job is not a failed Session

- **WHEN** the first AgentJob result of a session is `failed`
- **THEN** the history SHALL present that failure as the launch result of the session's first execution
- **AND** the AgentSession itself MUST NOT be labeled failed solely because its launch Job failed

#### Scenario: Grouping derives from outcomes

- **WHEN** the history groups session rows
- **THEN** each row's group SHALL be derived from its execution outcomes (first AgentJob result and latest Turn outcome) in result vocabulary

### Requirement: The first AgentJob result is presented per row

Each history row SHALL show the outcome of the session's first AgentJob — supplied by the session's first AgentTurn — as the launch result, distinct from the latest Turn outcome. Later Turns MUST NOT rewrite the presented launch result.

#### Scenario: Launch result resolves from the first Turn

- **WHEN** a session's first AgentTurn has reached a terminal result
- **THEN** the row SHALL present that result as the first AgentJob (launch) result

#### Scenario: Follow-up Turns do not rewrite the launch result

- **WHEN** later Turns of the session reach terminal results after the first
- **THEN** the row SHALL keep presenting the first AgentTurn's result as the launch result
- **AND** the latest Turn outcome SHALL be presented as a separate signal

### Requirement: The documented AgentJob result-view gap is closed

`docs/web-ui.md` SHALL no longer state in the Agents implementation gaps that AgentJob has no result view separate from its continuing AgentSession.

#### Scenario: Gap list no longer claims the missing result view

- **WHEN** the Agents implementation-gap list in `docs/web-ui.md` is read after this change
- **THEN** it MUST NOT contain the claim that AgentJob has no result view separate from its continuing AgentSession
