## MODIFIED Requirements

### Requirement: Remove run_ralph_loop Tool and References

The `run_ralph_loop` tool SHALL be removed entirely: tool file deleted, import removed from Main Agent, tool registration removed from tool registry, and all prompt references deleted.

#### Scenario: tool file deleted
- **WHEN** the codebase is inspected
- **THEN** the file `tools/run-ralph-loop.ts` SHALL NOT exist

#### Scenario: no run_ralph_loop references in Main Agent
- **WHEN** main-agent.ts is inspected
- **THEN** it SHALL NOT import or reference `createRunRalphLoopTool` or `run_ralph_loop`

#### Scenario: no run_ralph_loop references in system prompt
- **WHEN** the Main Agent system prompt is generated
- **THEN** it SHALL NOT contain the string "run_ralph_loop" or instructions to use the run_ralph_loop tool

#### Scenario: existing prompt structure preserved
- **WHEN** the system prompt is compared to the current version
- **THEN** at least 80% of the original content SHALL remain unchanged, excluding only the removed deprecated tool references
