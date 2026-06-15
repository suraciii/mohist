## ADDED Requirements

### Requirement: REQ-SSA-001 Agent reads delta specs from the change folder

The agent SHALL read all delta spec files from `openspec/changes/issue-{number}/specs/` before performing any merge operations. The agent SHALL discover spec files by traversing the `specs/` subdirectory of the active change folder.

#### Scenario: Agent discovers and reads delta specs
- **WHEN** `integrate:spec-sync` runs for a change that has delta specs under `openspec/changes/issue-{number}/specs/`
- **THEN** the agent SHALL list all `.md` files under that directory
- **AND** the agent SHALL read each file's content into memory before merging

#### Scenario: Missing delta specs directory is a failure
- **WHEN** `integrate:spec-sync` runs for a change that has no `specs/` directory under the change folder
- **THEN** the agent SHALL report failure with a clear message that no delta specs exist
- **AND** the task SHALL NOT attempt to write to `openspec/specs/`

### Requirement: REQ-SSA-002 Agent reads existing main specs from the canonical location

The agent SHALL read existing main spec files from `openspec/specs/` for every capability referenced by the delta specs. When a delta spec refers to a capability that has no existing main spec, the agent SHALL treat it as entirely new.

#### Scenario: Agent reads matching main spec for a modified capability
- **WHEN** a delta spec targets capability `workflow-definition`
- **AND** `openspec/specs/workflow-definition/spec.md` exists
- **THEN** the agent SHALL read the full content of that main spec file before merging
- **AND** the agent SHALL use the main spec content to resolve MODIFIED and REMOVED intent

#### Scenario: No main spec exists for a new capability
- **WHEN** a delta spec targets a capability with no matching directory under `openspec/specs/`
- **THEN** the agent SHALL treat the delta spec as introducing a new capability
- **AND** the agent SHALL create the capability directory under `openspec/specs/` when writing

#### Scenario: Main spec exists but has malformed content
- **WHEN** a main spec file exists but cannot be parsed as a valid spec
- **THEN** the agent SHALL report the parse failure with the affected capability and file path
- **AND** the agent SHALL NOT proceed with merge for that capability

### Requirement: REQ-SSA-003 Agent parses delta sections by operation type

The agent SHALL parse each delta spec file into its constituent operations: ADDED, MODIFIED, REMOVED, and RENAMED. Each parsed operation SHALL capture the requirement name, full requirement text, all scenarios, and any renaming FROM/TO metadata.

#### Scenario: Delta spec with ADDED requirements only
- **WHEN** a delta spec contains only `## ADDED Requirements` with one or more `### Requirement:` blocks
- **THEN** the agent SHALL parse each requirement block with its name, description, and all scenarios
- **AND** each parsed requirement SHALL be classified as an ADDED operation

#### Scenario: Delta spec with mixed operation types
- **WHEN** a delta spec contains ADDED, MODIFIED, and REMOVED sections
- **THEN** the agent SHALL parse each section independently
- **AND** each requirement SHALL be classified by the section header it appears under

#### Scenario: Delta spec with RENAMED requirements
- **WHEN** a delta spec contains a `## RENAMED Requirements` section
- **THEN** the agent SHALL parse the FROM name and TO name for each renamed requirement
- **AND** the agent SHALL validate that the FROM name exists in the main spec before applying the rename

### Requirement: REQ-SSA-004 Agent resolves obvious delta classification mistakes

The agent SHALL detect and resolve obvious requirement-level delta classification mistakes during merge. When a MODIFIED requirement has no matching source requirement in the main spec, no rename ambiguity, and does not duplicate an existing target name, the agent SHALL reclassify it as ADDED and record the correction.

#### Scenario: MODIFIED requirement reclassified as ADDED when source is absent
- **WHEN** the agent processes a MODIFIED requirement
- **AND** the main spec has no requirement with a matching name
- **AND** no RENAMED operation in the delta maps to that source name
- **AND** the target requirement name does not already exist in the main spec
- **THEN** the agent SHALL reclassify the operation from MODIFIED to ADDED
- **AND** the agent SHALL include the correction in the merge report with capability, requirement name, original operation, resolved operation, and reason

#### Scenario: MODIFIED requirement with matching source is applied as-is
- **WHEN** the agent processes a MODIFIED requirement
- **AND** the main spec has a matching source requirement by name
- **THEN** the agent SHALL apply it as a genuine modification
- **AND** no correction SHALL be recorded

#### Scenario: REMOVED requirement with unknown source fails the sync
- **WHEN** the agent processes a REMOVED requirement
- **AND** the main spec has no matching source requirement
- **THEN** the agent SHALL fail the sync with structured conflict output naming the capability, requirement, and missing-source reason
- **AND** the agent SHALL NOT silently skip the REMOVED operation

#### Scenario: RENAMED FROM with unknown source fails the sync
- **WHEN** the agent processes a RENAMED FROM requirement
- **AND** the main spec has no matching source requirement for the FROM name
- **THEN** the agent SHALL fail the sync with structured conflict output naming the capability, FROM name, and missing-source reason
- **AND** the agent SHALL NOT apply the rename or invent a source requirement

### Requirement: REQ-SSA-005 Agent merges delta intent into main specs

The agent SHALL apply each resolved operation to the corresponding main spec according to its type: ADDED requirements are appended, MODIFIED requirements replace their matching source, REMOVED requirements are deleted, and RENAMED requirements have their header name changed.

#### Scenario: ADDED requirement is appended to main spec
- **WHEN** the agent applies an ADDED operation to a main spec
- **THEN** the requirement block with all scenarios SHALL be appended to the main spec content after existing requirements
- **AND** the existing requirements in the main spec SHALL remain unchanged

#### Scenario: MODIFIED requirement replaces matching source
- **WHEN** the agent applies a MODIFIED operation to a main spec
- **AND** a matching source requirement exists by name
- **THEN** the entire source requirement block with all scenarios SHALL be replaced by the delta requirement block
- **AND** other requirements in the main spec SHALL remain unchanged

#### Scenario: REMOVED requirement is deleted from main spec
- **WHEN** the agent applies a REMOVED operation to a main spec
- **AND** a matching source requirement exists by name
- **THEN** the entire requirement block with all scenarios SHALL be removed from the main spec
- **AND** other requirements in the main spec SHALL remain unchanged

#### Scenario: RENAMED requirement changes requirement header only
- **WHEN** the agent applies a RENAMED operation to a main spec
- **AND** a matching source requirement exists for the FROM name
- **THEN** the requirement header SHALL change to the TO name
- **AND** the requirement description and scenarios SHALL remain unchanged

### Requirement: REQ-SSA-006 Agent writes merged specs to the canonical openspec/specs/ directory

The agent SHALL write merged spec files to `openspec/specs/{capability}/spec.md`. The agent SHALL create capability directories under `openspec/specs/` when they do not already exist.

#### Scenario: Merged spec written to canonical location
- **WHEN** the agent completes a merge for capability `spec-sync-agent`
- **THEN** the merged content SHALL be written to `openspec/specs/spec-sync-agent/spec.md`
- **AND** the parent directory SHALL be created if it does not exist

#### Scenario: Existing main spec is overwritten with merged content
- **WHEN** `openspec/specs/workflow-definition/spec.md` already exists
- **AND** the agent applies delta operations to that capability
- **THEN** the existing file SHALL be overwritten with the merged content
- **AND** the previous file content SHALL NOT be preserved at the canonical path

### Requirement: REQ-SSA-007 Agent validates merged spec structure before writing

The agent SHALL validate the merged spec structure before writing to `openspec/specs/`. Validation SHALL detect duplicate requirement headers, missing scenarios, malformed structure, and requirement blocks that still contain delta section headers.

#### Scenario: Duplicate requirement headers are rejected
- **WHEN** the merged spec content contains two requirement blocks with the same name
- **THEN** the agent SHALL fail the sync with a validation error naming the capability and the duplicate requirement
- **AND** the invalid content SHALL NOT be written to `openspec/specs/`

#### Scenario: Requirement without scenarios is rejected
- **WHEN** the merged spec content contains a requirement block with no `#### Scenario:` entries
- **THEN** the agent SHALL fail the sync with a validation error naming the capability and the affected requirement
- **AND** the invalid content SHALL NOT be written to `openspec/specs/`

#### Scenario: Delta section headers in merged output are rejected
- **WHEN** the merged spec content contains `## ADDED`, `## MODIFIED`, `## REMOVED`, or `## RENAMED` headers
- **THEN** the agent SHALL fail the sync with a validation error naming the capability and the residual delta header
- **AND** the invalid content SHALL NOT be written to `openspec/specs/`

#### Scenario: Valid merged spec is written successfully
- **WHEN** the merged spec content passes all validation checks
- **THEN** the agent SHALL write the content to `openspec/specs/{capability}/spec.md`
- **AND** the task SHALL report success with the written capability path

### Requirement: REQ-SSA-008 Agent reports merge results as structured transient output

The agent SHALL report merge results as structured task output. The output SHALL include what was added, modified, removed, and renamed per capability, along with any corrections that were applied. The output SHALL NOT be a durable workflow artifact.

#### Scenario: Merge report includes all applied operations
- **WHEN** the agent completes a successful spec sync
- **THEN** the task output SHALL list every applied operation grouped by capability
- **AND** each entry SHALL include the operation type, requirement name, and target capability

#### Scenario: Corrections are included in merge report
- **WHEN** the agent reclassifies a MODIFIED requirement as ADDED
- **THEN** the task output SHALL include a corrections section
- **AND** each correction SHALL include capability, requirement name, original operation, resolved operation, and reason

#### Scenario: Merge report is transient, not a durable artifact
- **WHEN** the agent produces a merge report
- **THEN** the output SHALL be stored as task `output` or workflow log data
- **AND** the output SHALL NOT be listed in the task's durable artifact paths

### Requirement: REQ-SSA-009 Agent does not create or write to workspace/specs/

The agent SHALL NOT create, write to, or reference a `{workspace}/specs/` directory. The only output destination for merged specs SHALL be `openspec/specs/`.

#### Scenario: No workspace specs directory is created
- **WHEN** `integrate:spec-sync` completes as an agent task
- **THEN** no `{workspace}/specs/` directory SHALL exist as a side effect of spec sync
- **AND** all spec placement SHALL be limited to `openspec/specs/`
