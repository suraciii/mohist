## MODIFIED Requirements

### Requirement: OpenSpec workflow structure
The system SHALL support a 4-stage workflow for OpenSpec-style changes.

**Stages:**
1. **plan** - Generate Change artifacts + self-review
2. **review** - Human review (approval gate)
3. **build** - Ralph-style task execution
4. **check** - Automated testing + human acceptance + archival (approval gate)

**Stage Timeout Resolution:** Each stage's timeout is determined by:
1. Explicit `timeout` field in project's `workflow.yaml` (if present)
2. Otherwise, the value of `agent.stageTimeout` from config (default 3600s)

#### Scenario: Default OpenSpec workflow
- **WHEN** an issue starts with `mo propose` or `mo issue start`
- **AND** the system detects no existing Change (or creates new version)
- **THEN** it follows the 4-stage workflow
- **AND** each stage has specific responsibilities
- **AND** stages without an explicit `workflow.yaml` timeout use `agent.stageTimeout` from config

#### Scenario: Workflow YAML overrides config default
- **WHEN** project's `workflow.yaml` specifies `timeout: 1800` for the build stage
- **THEN** the build stage uses 1800s timeout regardless of `agent.stageTimeout` config value
- **AND** other stages without explicit timeouts still fall back to config
