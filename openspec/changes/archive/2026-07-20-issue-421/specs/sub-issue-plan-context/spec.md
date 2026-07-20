### Requirement: Sub-issue Plan input includes parent issue background

Every Plan-stage Inline Agent input for a sub-issue SHALL include the parent issue's title and body as background context alongside the sub-issue's own body and comments. The parent title and body MUST be available to the Inline Agent without being copied into the sub-issue body.

#### Scenario: Planning a sub-issue receives its parent's requirement background

- **WHEN** a sub-issue enters the Plan stage and its parent has a title and body
- **THEN** the Plan-stage Inline Agent input SHALL contain that parent title and body
- **AND** the input SHALL continue to contain the sub-issue's own body and comments

#### Scenario: Parent-only background is available to the Plan agent

- **WHEN** a parent body contains requirement background that is absent from its sub-issue's body
- **AND** the sub-issue enters the Plan stage
- **THEN** the Plan-stage Inline Agent input SHALL contain that parent-only requirement background

### Requirement: Parent context is identified as read-only background

The Plan-stage Inline Agent input SHALL clearly identify the parent title and body as background context and SHALL identify the sub-issue body as the authority for the sub-issue's delivery scope. The parent context MUST NOT include parent comments or parent artifacts, and MUST NOT include any sibling sub-issue's body, comments, or artifacts.

#### Scenario: Parent and child content imply different delivery scopes

- **WHEN** a parent body describes the overall requirement and a sub-issue body limits delivery to one part of that requirement
- **THEN** the Plan-stage Inline Agent input SHALL identify the parent content as background
- **AND** the input SHALL identify the sub-issue body as authoritative for the planned delivery scope

#### Scenario: Parent comments and artifacts are excluded

- **WHEN** a sub-issue's parent has comments and artifacts in addition to its title and body
- **THEN** the sub-issue's Plan-stage Inline Agent input SHALL include the parent title and body
- **AND** it SHALL NOT include the parent's comments or artifacts as parent context

#### Scenario: Sibling issue content is excluded

- **WHEN** two or more sub-issues share the same parent and one sub-issue enters the Plan stage
- **THEN** that sub-issue's Plan-stage Inline Agent input SHALL NOT include any sibling sub-issue's body, comments, or artifacts

### Requirement: Parent background injection is limited to sub-issue Plan work

Parent background context SHALL be added only to Plan-stage Inline Agent input for issues that currently have a parent. Plan-stage Inline Agent input for an issue without a parent SHALL remain unchanged, and non-Plan work for a sub-issue SHALL NOT receive parent background context. Context injection MUST NOT change workflow lifecycle, approval, stage progression, or parent-child status behavior.

#### Scenario: Ordinary issue Plan input is unchanged

- **WHEN** an issue without a parent enters the Plan stage
- **THEN** its Inline Agent input SHALL remain the existing issue body, comments, and task prompt without parent background context

#### Scenario: A sub-issue enters a non-Plan stage

- **WHEN** a sub-issue dispatches Inline Agent work in Build, Check, or Integrate
- **THEN** that work SHALL NOT receive the parent issue's title or body as parent background context

#### Scenario: Injecting context does not alter lifecycle state

- **WHEN** parent background context is provided to a sub-issue's Plan-stage Inline Agent
- **THEN** the sub-issue and parent SHALL retain the workflow, approval, stage progression, and status behavior they would have had without the additional context
