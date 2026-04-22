## MODIFIED Requirements

### Requirement: Pipeline plan stage with structured prompts
The system SHALL use structured prompts with template skeletons for artifact generation in the plan stage.

#### Scenario: All artifacts generated with structured prompts
- **WHEN** the plan stage runs for an issue
- **THEN** each artifact round uses a structured prompt with template, dependencies, output path, and instructions
- **AND** the agent receives a skeleton to fill rather than writing from scratch
- **AND** all previously generated artifacts are listed as dependencies for subsequent rounds

#### Scenario: Retry on missing artifact
- **WHEN** an artifact round completes but the file is missing
- **THEN** a retry prompt is sent with focused write_file instructions
- **AND** if retry succeeds, the pipeline continues normally
- **AND** if retry fails, the stage returns failure with a clear message
