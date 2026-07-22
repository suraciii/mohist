### Requirement: Profile owns identity and presentation
A Workflow Profile SHALL carry its `id`, `name`, `description`, and one Workflow Definition as distinct profile assets. Definition loading, persistence, and responses MUST NOT derive Profile identity or presentation metadata from the Definition.

#### Scenario: Load a persisted Profile
- **WHEN** a Project or Issue loads a saved Workflow Profile
- **THEN** its `id`, `name`, and `description` are read from the Profile asset and its executable content is read from its Definition

### Requirement: Profile selection uses profile identity
Project defaults, Issue selections, and active WorkflowRuns SHALL resolve a Profile by Profile identity and then load that Profile's Definition. A later stage entry for an active WorkflowRun MUST use the same Profile/Definition boundary as initial run creation.

#### Scenario: Enter a later stage after a Profile edit
- **WHEN** an active WorkflowRun enters a later stage after its selected Profile's Definition has changed
- **THEN** the stage is loaded from that Profile's current Definition without reading identity or metadata from the Definition

### Requirement: Built-in catalog owns built-in metadata
The built-in Profile catalog SHALL provide the names, descriptions, and default designation for `mohist/local` and `mohist/github-pr`. Their Definitions MUST preserve their existing executable stages and selection behavior while containing no catalog metadata.

#### Scenario: List built-in Profiles
- **WHEN** a client lists built-in Workflow Profiles
- **THEN** it receives the existing Local and GitHub PR names, descriptions, and default selection from the built-in catalog
