### Requirement: WatchEntry resource

The system SHALL persist a per-issue Agent watch declaration as a `WatchEntry` keyed by
`(ProjectId, IssueNumber, AgentId)` with a `State` of exactly `watching` or `muted`. For any
`(ProjectId, IssueNumber, AgentId)` there SHALL be at most one `WatchEntry`; `watching` and
`muted` cannot coexist on the same triple. `WatchEntry` SHALL be owned by the Agent context;
the Issue aggregate SHALL NOT hold it, and issue details surface it only as a read projection.

#### Scenario: Unique state per issue-agent pair

- **WHEN** a `WatchEntry` already exists for `(ProjectId, IssueNumber, AgentId)`
- **THEN** no second `WatchEntry` for that triple may exist; any command affecting that Agent on
  that issue transitions the existing entry's `State` rather than creating a new row

### Requirement: `watch add` state transitions

`mo issue watch add <issue> --agent <name>` SHALL move the `(issue, agent)` declaration to
`watching`: from no declaration it SHALL create a `watching` entry, and from `muted` it SHALL
transition to `watching`. When the declaration is already `watching`, the command SHALL be
idempotent and report the current `watching` state without error.

#### Scenario: Add with no prior declaration

- **WHEN** no `WatchEntry` exists for `(project, issue, agent)` and `watch add` is issued
- **THEN** a `WatchEntry` with `State = watching` is created

#### Scenario: Add unmutes a muted agent

- **WHEN** the `(project, issue, agent)` declaration is `muted` and `watch add` is issued
- **THEN** the declaration transitions to `watching`, lifting the mute on that issue

#### Scenario: Add is idempotent when already watching

- **WHEN** the `(project, issue, agent)` declaration is already `watching` and `watch add` is issued
- **THEN** no change is made and the command reports the current `watching` state

### Requirement: `watch remove` state transitions

`mo issue watch remove <issue> --agent <name>` SHALL delete a `watching` declaration, and SHALL
create a `muted` declaration when the Agent is otherwise covered only by a project-level routing
rule (i.e., there was no prior declaration). When the declaration is already `muted`, the command
SHALL be idempotent and report the current `muted` state without error. A `muted` declaration
SHALL leave the project-level routing rule itself untouched — it is an exception scoped to this
single issue only.

#### Scenario: Remove a watching declaration

- **WHEN** the `(project, issue, agent)` declaration is `watching` and `watch remove` is issued
- **THEN** the declaration is deleted; that Agent no longer auto-launches on that issue

#### Scenario: Remove records a mute against a project rule

- **WHEN** no declaration exists for `(project, issue, agent)` and `watch remove` is issued
- **THEN** a `WatchEntry` with `State = muted` is created, excepting this issue from any
  project-level routing rule that would otherwise launch that Agent

#### Scenario: Remove is idempotent when already muted

- **WHEN** the `(project, issue, agent)` declaration is already `muted` and `watch remove` is issued
- **THEN** no change is made and the command reports the current `muted` state

### Requirement: `watch list`

`mo issue watch list <issue>` SHALL list the issue's `watching` Agents and its `muted` Agents as
two distinct groups.

#### Scenario: List separates watching and muted

- **WHEN** an issue has some Agents `watching` and some `muted`
- **THEN** `watch list` displays both groups, with each Agent appearing in exactly one group

### Requirement: Active-Agent validation

`watch add` and `watch remove` SHALL validate that the named Agent exists in the project and is
active. An Agent that does not exist SHALL be rejected with an `agent_not_found` outcome, and an
Agent whose status is `archived` SHALL be rejected with an `agent_archived` outcome; neither
SHALL mutate any `WatchEntry`. Agent name resolution (including archived Agents) SHALL follow the
existing agent resolution path used elsewhere in the CLI.

#### Scenario: Reject unknown agent

- **WHEN** `watch add` (or `watch remove`) names an Agent that does not exist in the project
- **THEN** the command fails with an `agent_not_found` error and no `WatchEntry` is created or
  modified

#### Scenario: Reject archived agent

- **WHEN** `watch add` (or `watch remove`) names an Agent whose status is `archived`
- **THEN** the command fails with an `agent_archived` error and no `WatchEntry` is created or
  modified

### Requirement: Watch projection in issue detail

The issue read model SHALL carry the issue's `watching` Agents and `muted` Agents as two
projected groups, assembled from `WatchEntry` rows scoped to `(ProjectId, IssueNumber)`. Both
`mo issue view` and the Web issue detail SHALL render these as read-only "watching" and "muted"
sections. The Web rendering SHALL be read-only for this change (operations go through the CLI).

#### Scenario: CLI view shows watching and muted

- **WHEN** an issue has `watching` and/or `muted` declarations and `mo issue view` is issued
- **THEN** the output shows the watching Agents and muted Agents as distinct sections

#### Scenario: Web detail shows watching and muted read-only

- **WHEN** the Web issue detail is opened for an issue that has `watching` and/or `muted`
  declarations
- **THEN** the detail surfaces the watching and muted Agents from the read model, without offering
  in-page add/remove controls
