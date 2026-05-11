## MODIFIED Requirements

### Requirement: default runnable workflow stages match executed pipeline (REQ-001)
The built-in default workflow definition SHALL only declare stages that the runner can actually execute.

#### Scenario: Explore is not declared as a default runnable stage
- **WHEN** the system loads the built-in default workflow
- **THEN** the declared runnable stages do not include `explore`

#### Scenario: Default runnable workflow matches execution order
- **WHEN** no project-specific workflow overrides are present
- **THEN** the default runnable stage list is `plan -> build -> check -> integrate -> done`
- **AND** the declared workflow does not imply a hidden or missing runner stage
