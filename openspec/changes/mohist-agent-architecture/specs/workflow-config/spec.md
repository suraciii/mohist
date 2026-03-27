## ADDED Requirements

### Requirement: workflow.yaml location
The workflow configuration SHALL be located at `.mohist/workflow.yaml` in the project root. Mohist server SHALL load this file when processing issues for the project.

#### Scenario: Load workflow config
- **WHEN** Mohist server starts or an issue is created for a project
- **THEN** the system SHALL read `.mohist/workflow.yaml` from the project root
- **THEN** the stages SHALL be parsed in array order

#### Scenario: Missing workflow config
- **WHEN** no `.mohist/workflow.yaml` exists in the project
- **THEN** the system SHALL use a built-in default workflow

### Requirement: Stage definition
Each stage in workflow.yaml SHALL define: `agent` (agent type reference), `description` (natural language description for LLM), `expects` (expected output description for LLM), and `gate_after` (either `approve` or `auto`).

#### Scenario: Valid stage definition
- **WHEN** a stage defines agent, description, expects, and gate_after
- **THEN** the system SHALL parse it as a valid stage

#### Scenario: gate_after values
- **WHEN** gate_after is `approve`
- **THEN** the stage SHALL require user approval before advancing
- **WHEN** gate_after is `auto`
- **THEN** the stage SHALL advance automatically after sub-agent completion

### Requirement: Stage ordering
Stages SHALL be executed in the order they appear in the workflow.yaml array. The system SHALL NOT support parallel stages or conditional branching.

#### Scenario: Sequential execution
- **WHEN** workflow.yaml defines stages [explore, plan, dev, verify]
- **THEN** the stages SHALL execute in that exact order

### Requirement: Two-audience design
workflow.yaml fields SHALL serve two audiences: Code (gate_after, agent) and LLM (description, expects). Fields for Code SHALL control execution behavior. Fields for LLM SHALL provide semantic context for decision-making.

#### Scenario: Code reads gate_after
- **WHEN** a sub-agent completes a stage
- **THEN** the runtime SHALL read gate_after to decide whether to pause or auto-advance

#### Scenario: LLM reads description and expects
- **WHEN** the Main Agent evaluates a sub-agent's output
- **THEN** it SHALL use the stage's description and expects to judge output quality
