## MODIFIED Requirements

### Requirement: Epic detail page lifecycle actions

The Epic detail page header SHALL surface a single prominent primary lifecycle action chosen by the epic's status and readiness, consistent with the Pause/Resume/Close lifecycle: `paused` SHALL display a primary "Resume" action; `idle` SHALL display a primary "Start Epic" action; `running` SHALL display a primary "Pause" action; `done`/`closed` SHALL display no lifecycle action. When the epic is ready to mark done (`readyToMarkDone`) and is neither paused nor terminal, "Mark Done" SHALL be surfaced as the prominent primary action in place of the status-based Start/Pause action. Invoking Start Epic / Pause / Resume / Mark Done SHALL call the corresponding backend API and, on success, SHALL refresh the epic state and dashboard.

When "Mark Done" is rendered but disabled, the page SHALL display a visible, on-screen reason explaining why the epic cannot be marked done (e.g. it is paused, or N linked issues remain unfinished). The disabled reason SHALL NOT depend on a hover-only tooltip or a `title` attribute, so that touch device users can read the reason without a pointer hover.

#### Scenario: Idle epic shows Start Epic
- **WHEN** the Epic detail page is rendered for an `idle` epic that is not ready to mark done
- **THEN** the page header SHALL display a "Start Epic" primary action
- **AND** invoking it SHALL call the start API and refresh state on success

#### Scenario: Running epic shows Pause
- **WHEN** the Epic detail page is rendered for a `running` epic that is not ready to mark done
- **THEN** the page header SHALL display a "Pause" primary action
- **AND** invoking it SHALL call the pause API and refresh state on success

#### Scenario: Paused epic shows Resume
- **WHEN** the Epic detail page is rendered for a `paused` epic
- **THEN** the page header SHALL display a "Resume" primary action
- **AND** invoking it SHALL call the resume API and refresh state on success

#### Scenario: Ready epic shows Mark Done as the prominent action
- **WHEN** the Epic detail page is rendered for a non-paused, non-terminal epic whose `readyToMarkDone` is true
- **THEN** the page header SHALL surface "Mark Done" as the prominent primary action
- **AND** invoking it SHALL call the mark-done API and refresh state on success

#### Scenario: Paused ready epic keeps Resume primary
- **WHEN** the Epic detail page is rendered for a `paused` epic whose `readyToMarkDone` is true
- **THEN** the page header SHALL display "Resume" as the primary action
- **AND** SHALL NOT surface "Mark Done" as the prominent primary action (marking done is reached via Resume's re-evaluation)

#### Scenario: Terminal epic shows no lifecycle action
- **WHEN** the Epic detail page is rendered for a `done` or `closed` epic
- **THEN** the page header SHALL NOT display any Start/Pause/Resume/Mark Done lifecycle action

#### Scenario: Disabled Mark Done shows a visible reason on touch devices
- **WHEN** the Epic detail page is rendered for a non-terminal epic whose "Mark Done" action is disabled
- **THEN** the page SHALL display a visible on-screen reason explaining why it cannot be marked done
- **AND** the reason SHALL be readable without a pointer hover (e.g. without relying on a `title` tooltip)

#### Scenario: Disabled Mark Done reason reflects a paused epic
- **WHEN** the Epic detail page is rendered for an epic whose "Mark Done" action is disabled because it is paused
- **THEN** the visible reason SHALL indicate that the epic must be resumed before marking done

#### Scenario: Disabled Mark Done reason reflects unfinished linked issues
- **WHEN** the Epic detail page is rendered for an epic whose "Mark Done" action is disabled because linked issues remain unfinished
- **THEN** the visible reason SHALL state how many linked issues remain unfinished
