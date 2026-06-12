## ADDED Requirements

### Requirement: Issue Detail shows latest WorkflowArtifacts index
Issue Detail SHALL provide a compact latest-artifacts index for the issue's current or recent workflow run. The index SHALL group by artifact path and show the newest WorkflowArtifact for each path.

#### Scenario: Latest artifacts render by path
- **WHEN** an issue has recorded WorkflowArtifacts for proposal, design, tasks, review, or related outputs
- **THEN** Issue Detail SHALL render a latest-artifacts index grouped by path
- **AND** each item SHALL open the immutable recorded content for the latest artifact version

#### Scenario: Latest review points to newest review run
- **WHEN** `ai-review.1` and `ai-review.2` both recorded `review.md`
- **THEN** the latest-artifacts index SHALL show `review.md` for the `ai-review.2` version
- **AND** it SHALL NOT hide the older version from the producing task row

### Requirement: Issue workflow task rows show produced artifacts
The primary workflow timeline and task progress surfaces SHALL show WorkflowArtifacts on the task run that produced them. Task artifact links SHALL open the immutable recorded artifact version, including versions later superseded by another task run with the same path.

#### Scenario: Task row shows its artifacts
- **WHEN** a workflow task run produced one or more WorkflowArtifacts
- **THEN** the task row SHALL display those artifacts as outputs of that task run
- **AND** selecting an artifact SHALL open recorded content for that artifact id

#### Scenario: Historical review remains accessible from task row
- **WHEN** Check has `ai-review.1`, `fix-review-findings`, and `ai-review.2` task rows
- **THEN** the `ai-review.1` row SHALL still expose its recorded `review.md`
- **AND** the `ai-review.2` row SHALL expose its later recorded `review.md`

### Requirement: Web UI renders directory artifacts as browsable collections
The Web UI SHALL render directory WorkflowArtifacts as one collection in latest and task artifact views. Users SHALL be able to browse contained recorded files without flooding the top-level artifact list.

#### Scenario: Directory artifact is one top-level item
- **WHEN** an issue has a recorded directory artifact such as `specs/`
- **THEN** latest and task artifact views SHALL show one collection item for that directory
- **AND** they SHALL NOT render every contained file as a peer top-level artifact

#### Scenario: User opens contained directory file
- **WHEN** a user opens a file inside a directory artifact collection
- **THEN** the Web UI SHALL request and display the recorded content for that immutable artifact version
- **AND** it SHALL make clear that the content belongs to the recorded artifact, not the mutable workspace file

### Requirement: Artifact views use Artifact language
The Web UI SHALL label the feature using WorkflowArtifact or Artifact language. It SHALL NOT present the model, route, component, or user-facing concept as Snapshot.

#### Scenario: Artifact terminology is used
- **WHEN** Issue Detail renders latest and per-task recorded outputs
- **THEN** headings, labels, DTO usage, and component names SHALL use Artifact or WorkflowArtifact terminology
- **AND** they SHALL NOT use Snapshot terminology for this feature
