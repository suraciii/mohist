## ADDED Requirements

### Requirement: Epic lifecycle documentation uses the five-state self-driving model

The user-facing Epic documentation (`docs/epics.md` lifecycle section and field table) SHALL describe the self-driving lifecycle states `idle`, `running`, `paused`, `done`, and `closed`, each with its meaning and entry condition. The documentation SHALL NOT describe the legacy three-state `active` / `done` / `closed` model, and the Epic field table SHALL NOT list `status` as `active / done / closed`. The `idle` state SHALL be documented as the default state of a newly created epic (not `active`), and `done` / `closed` SHALL be documented as terminal.

#### Scenario: Lifecycle section documents the five states

- **WHEN** a reader opens the Epic lifecycle section of `docs/epics.md`
- **THEN** the section SHALL list the states `idle`, `running`, `paused`, `done`, and `closed`
- **AND** SHALL state the meaning and entry condition for each state

#### Scenario: Legacy three-state model is removed

- **WHEN** a reader searches `docs/epics.md` for the Epic status model
- **THEN** the documentation SHALL NOT present `active` as an Epic status
- **AND** the field table SHALL NOT describe `status` as `active / done / closed`

#### Scenario: Default state is documented as idle

- **WHEN** a reader looks up what state a new Epic starts in
- **THEN** the documentation SHALL state that a newly created Epic is `idle` by default
- **AND** SHALL state that it does not autonomously start until explicitly started

### Requirement: Epic Start, Pause, and Resume are documented across all surfaces

The Epic documentation SHALL document the Start, Pause, and Resume actions and SHALL present each action consistently across the CLI, the Web UI, and the HTTP API. The documentation SHALL state that Start transitions an `idle` epic to `running` and attempts to advance the first startable linked issue; that Pause transitions a `running` epic to `paused` without interrupting an in-progress linked issue; and that Resume transitions a `paused` epic back to `running` and re-evaluates readiness and advancement. The documentation SHALL state that Start, Pause, and Resume are idempotent.

#### Scenario: Start is documented for CLI, Web UI, and API

- **WHEN** a reader looks up how to start an Epic
- **THEN** the documentation SHALL show the `mo epic start` command, the Web UI Start Epic action, and the HTTP API start endpoint
- **AND** SHALL state that Start attempts to advance the first startable linked issue

#### Scenario: Pause and Resume are documented as non-interrupting and re-evaluating

- **WHEN** a reader looks up Pause and Resume
- **THEN** the documentation SHALL state that Pause stops future advancement but does not interrupt the in-progress linked issue
- **AND** SHALL state that Resume re-evaluates readiness and advancement

#### Scenario: Idempotency is documented

- **WHEN** a reader looks up repeated Start, Pause, or Resume invocations
- **THEN** the documentation SHALL state that each action is idempotent (a repeat of the current state is a no-op that does not error)

### Requirement: Autonomous advancement and running-but-idle are documented

The Epic documentation SHALL document autonomous advancement: a `running` epic advances the next startable linked issue when the in-progress linked issue reaches a terminal state, while `idle` and `paused` epics do not auto-advance. The documentation SHALL document running-but-idle as an observable situation explained by `nextIssueReason`, and SHALL explicitly state that running-but-idle is not a separate Epic state.

#### Scenario: Auto-advancement is documented for running epics

- **WHEN** a reader looks up how a running Epic progresses through its linked issues
- **THEN** the documentation SHALL state that a `running` epic advances the next startable linked issue when the in-progress one reaches a terminal state
- **AND** SHALL state that `idle` and `paused` epics do not auto-advance

#### Scenario: Running-but-idle is documented as a situation, not a state

- **WHEN** a reader looks up what happens when a running Epic has no startable next issue
- **THEN** the documentation SHALL describe running-but-idle as an observable situation
- **AND** SHALL state that `nextIssueReason` explains why nothing is currently advancing
- **AND** SHALL NOT list running-but-idle as an Epic lifecycle state

### Requirement: Epic's relationship to issue workflow is documented accurately

The Epic documentation SHALL NOT state that "Epic 只是组织工具，不参与执行" or otherwise frame the Epic as a purely static organizer that does not participate in execution. The documentation SHALL state that an Epic influences advancement of its linked issues, while each linked issue still runs its own workflow unchanged.

#### Scenario: Static-organizer framing is removed

- **WHEN** a reader looks up the relationship between Epic and workflow in `docs/epics.md`
- **THEN** the documentation SHALL NOT claim the Epic is only an organizing tool that does not participate in execution
- **AND** SHALL state that the Epic influences advancement of its linked issues

#### Scenario: Per-issue workflow rules are documented as unchanged

- **WHEN** a reader looks up how an Epic affects each linked issue's own workflow
- **THEN** the documentation SHALL state that each linked issue still runs its own workflow
- **AND** SHALL state that the Epic does not change an individual issue's workflow execution rules

### Requirement: Concepts documentation frames Epic as the self-driving planning unit

The `docs/concepts.md` Epic section SHALL reflect the self-driving role: an Epic is the planning and advancement unit for a product goal and its linked issues, a new Epic is `idle` by default, and Start begins autonomous progression. The section SHALL NOT frame the Epic merely as a passive collection or folder of issues.

#### Scenario: Concepts Epic section describes the self-driving role

- **WHEN** a reader opens the Epic section of `docs/concepts.md`
- **THEN** the section SHALL describe the Epic as the planning and advancement unit for a product goal
- **AND** SHALL state that a new Epic is `idle` by default and begins autonomous progression via Start

#### Scenario: Concepts Epic section does not frame Epic as a passive folder

- **WHEN** a reader opens the Epic section of `docs/concepts.md`
- **THEN** the section SHALL NOT frame the Epic solely as a static collection of issues
- **AND** SHALL NOT imply the Epic has no role in execution

### Requirement: Web UI documentation describes the self-driving Epic surfaces

The `docs/web-ui.md` Epics page section SHALL describe the list-page state groups (`Running`, `Ready to start`, `Waiting / Blocked`, `Idle / Empty`) and the detail-page lifecycle actions (`Start Epic`, `Pause`, `Resume`, `Mark Done`) consistent with the actual Web UI. The documented operation paths SHALL match the Web UI implemented in `packages/web`.

#### Scenario: List-page state groups are documented

- **WHEN** a reader opens the Epics page section of `docs/web-ui.md`
- **THEN** the section SHALL describe the `Running`, `Ready to start`, `Waiting / Blocked`, and `Idle / Empty` list-page groups
- **AND** the described groups SHALL match the actual Web UI

#### Scenario: Detail-page lifecycle actions are documented

- **WHEN** a reader opens the Epics page section of `docs/web-ui.md`
- **THEN** the section SHALL describe the `Start Epic`, `Pause`, `Resume`, and `Mark Done` detail-page actions
- **AND** the documented operation paths SHALL match the actual Web UI in `packages/web`

### Requirement: CLI reference documents the real epic subcommands

The `docs/cli-reference.md` SHALL document the real `mo epic` subcommands, including `start`, `pause`, and `resume`. The documentation SHALL NOT carry the stale note that Epic management is unsupported from the CLI.

#### Scenario: Epic subcommands are documented

- **WHEN** a reader opens the Epic section of `docs/cli-reference.md`
- **THEN** the section SHALL document the `mo epic` subcommands, including `start`, `pause`, and `resume`

#### Scenario: Stale unsupported note is removed

- **WHEN** a reader searches `docs/cli-reference.md` for Epic management availability
- **THEN** the documentation SHALL NOT state that Epic management is unsupported from the CLI
- **AND** SHALL NOT direct the user to the API as the only way to manage Epics

### Requirement: Documentation and Web UI copy stay aligned with the self-driving model

The documentation and the Web UI copy SHALL be audited for stragglers that imply the legacy static model, and any such stragglers SHALL be aligned. A bare `Active` Epic status label, a bare `Start` label used where the Epic lifecycle `Start Epic` action is meant, or a `No linked issues` message used in a way that implies the old model SHALL be corrected so the user-facing wording is consistent with the self-driving target experience across `docs/` and `packages/web`.

#### Scenario: Bare Active status label is not used to imply the legacy model

- **WHEN** the Web UI or documentation refers to a non-terminal Epic's status
- **THEN** it SHALL use the self-driving state labels (`idle`, `running`, `paused`)
- **AND** SHALL NOT use a bare `Active` label to imply the legacy `active` status

#### Scenario: Bare Start label does not collide with Start Epic

- **WHEN** the Web UI or documentation labels the Epic lifecycle start action
- **THEN** it SHALL distinguish the Epic lifecycle `Start Epic` action from any per-issue `Start next issue` action
- **AND** SHALL NOT use an ambiguous bare `Start` label that could be mistaken for the legacy model

#### Scenario: No linked issues copy is consistent with the self-driving model

- **WHEN** the Web UI or documentation surfaces an empty Epic with no linked issues
- **THEN** the message SHALL be consistent with the self-driving target experience
- **AND** SHALL NOT imply the Epic is actively progressing
