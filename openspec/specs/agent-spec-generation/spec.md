## ADDED Requirements

### Requirement: Explore agent for spec generation
The system SHALL spawn an explore agent during the plan stage to analyze the issue and codebase, then generate complete Change artifacts.

#### Scenario: Generate change artifacts
- **WHEN** the plan stage is initiated for an issue
- **THEN** the system spawns an explore agent with the issue context
- **AND** the agent analyzes the codebase and requirements
- **AND** the agent generates:
  - `proposal.md` with background and motivation
  - `design.md` with technical approach
  - `specs/` directory with detailed requirements organized by capability

### Requirement: Spec file format compliance
The system SHALL ensure generated spec files follow the standard format with requirements and scenarios.

#### Scenario: Generate valid spec file
- **WHEN** the agent generates a spec file at `specs/{capability}/spec.md`
- **THEN** the file contains:
  - `## ADDED Requirements` section
  - `### Requirement: <name>` for each requirement
  - `#### Scenario: <name>` with WHEN/THEN format for each scenario
- **AND** all requirements use SHALL/MUST for normative statements
- **AND** every requirement has at least one scenario

### Requirement: Codebase-aware spec generation
The system SHALL generate specs that are informed by the actual codebase structure and existing patterns.

#### Scenario: Analyze codebase before generating
- **WHEN** generating specs for a new feature
- **THEN** the agent explores relevant code files
- **AND** identifies existing patterns and conventions
- **AND** generates specs that align with the existing architecture

### Requirement: Spec completeness validation
The system SHALL validate that generated specs cover the full scope of the issue.

#### Scenario: Validate spec coverage
- **WHEN** specs are generated
- **THEN** the system checks that all aspects of the issue are addressed
- **AND** if gaps are found, the agent is prompted to fill them
- **AND** the process repeats until coverage is complete
