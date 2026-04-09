## MODIFIED Requirements

### Requirement: System Prompt Without Deprecated Tool References

The Main Agent system prompt SHALL NOT reference the `run_ralph_loop` tool. All other existing prompt content SHALL be preserved.

#### Scenario: no run_ralph_loop references
- **WHEN** the Main Agent system prompt is generated
- **THEN** it SHALL NOT contain the string "run_ralph_loop" or instructions to use the run_ralph_loop tool

#### Scenario: existing prompt structure preserved
- **WHEN** the system prompt is compared to the current version
- **THEN** at least 80% of the original content SHALL remain unchanged, excluding only the removed deprecated tool references
