### Requirement: Top-level session command addressed by stable Session ID

`mo session` MUST be a top-level command group that addresses an AgentSession by its stable Session ID, regardless of whether the session originated from an Agent launch or a Workflow run. The commands `show`, `transcript`, `followup`, `compact`, `reset`, and `cancel` SHALL each take a Session ID argument.

#### Scenario: Show a session by id regardless of source
- **WHEN** `mo session show <session-id>` runs
- **THEN** the session summary is shown, whether the session is Agent-launch- or Workflow-originated

#### Scenario: Transcript by id
- **WHEN** `mo session transcript <session-id>` runs
- **THEN** the conversation transcript is shown

#### Scenario: Follow-up by id creates no job
- **WHEN** `mo session followup <session-id> --text "..."` runs
- **THEN** the follow-up is delivered without creating a TaskRun or AgentJob

### Requirement: Source-filtered list

`mo session list` MUST accept `--agent <agent>`, `--issue <number>`, and `--run <run-id>` as filters. Source is a filter for discovery; it MUST NOT create a separate command set.

#### Scenario: List by agent
- **WHEN** `mo session list --agent <agent>` runs
- **THEN** sessions launched by that agent are listed

#### Scenario: List by issue
- **WHEN** `mo session list --issue <number>` runs
- **THEN** sessions associated with that issue are listed

#### Scenario: List by run
- **WHEN** `mo session list --run <run-id>` runs
- **THEN** sessions for that workflow run are listed

### Requirement: Retire duplicate session command groups

The `mo agent session` and `mo issue session` command groups MUST be removed. Their actions are absorbed into the single top-level `mo session`; there SHALL NOT be two parallel capability sets that differ only by source.

#### Scenario: Agent session group removed
- **WHEN** a user runs `mo agent session show <session-id>`
- **THEN** the command is not found; the user uses `mo session show <session-id>` instead

#### Scenario: Issue session group removed
- **WHEN** a user runs `mo issue session show`
- **THEN** the command is not found; the user uses `mo session` addressed by Session ID instead

### Requirement: Cancel interrupts the runtime only

`mo session cancel <session-id>` MUST request interruption of the current Runtime execution only. It MUST NOT cancel, complete, or rewrite the owning AgentJob's lifecycle; the AgentJob remains the sole terminal authority for its own result.

#### Scenario: Cancel does not terminate the job
- **WHEN** `mo session cancel <session-id>` runs against a session whose owning AgentJob is running
- **THEN** the runtime execution is interrupted and the AgentJob's terminal state is left for the job owner to decide
