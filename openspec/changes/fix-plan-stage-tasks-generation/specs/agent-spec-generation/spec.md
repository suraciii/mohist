## MODIFIED Requirements

### Requirement: Structured artifact prompt generation
The system SHALL generate structured prompts for each artifact using XML-tagged sections.

#### Scenario: Prompt contains structured sections
- **WHEN** the system generates a prompt for any artifact type (proposal, specs, design, tasks)
- **THEN** the prompt SHALL contain these XML-tagged sections:
  - `<task>` — what to create, with artifact description
  - `<dependencies>` — list of completed artifact files to read, with full paths
  - `<output>` — explicit file path where to write
  - `<template>` — skeleton structure to fill in
  - `<instruction>` — detailed guidance from the artifact's .md instruction file
- **AND** the `<dependencies>` section SHALL only list artifacts that exist on disk
- **AND** the `<output>` section SHALL contain the absolute file path

#### Scenario: Template skeleton per artifact
- **WHEN** the system generates a prompt for an artifact
- **THEN** the `<template>` section SHALL contain a skeleton file for the agent to fill in
- **AND** the skeleton SHALL match the expected output format (markdown for proposal/specs/design, JSON structure for tasks)

### Requirement: Per-round artifact verification with retry
The system SHALL verify artifact existence after each generation round and provide a single retry on failure.

#### Scenario: Missing artifact detection and retry
- **WHEN** a plan stage round completes
- **AND** the expected artifact file does not exist
- **THEN** the system sends a retry prompt with explicit write_file instructions
- **AND** if the artifact still does not exist after retry, the round fails

#### Scenario: Successful verification
- **WHEN** a plan stage round completes
- **AND** the artifact file exists
- **THEN** the pipeline proceeds to the next round without retry
