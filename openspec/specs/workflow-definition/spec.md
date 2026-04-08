## ADDED Requirements

### Requirement: OpenSpec workflow structure
The system SHALL support a 4-stage workflow for OpenSpec-style changes.

**Stages:**
1. **plan** - Generate Change artifacts + self-review
2. **review** - Human review (approval gate)
3. **build** - Ralph-style task execution
4. **check** - Automated testing + human acceptance + archival (approval gate)

#### Scenario: Default OpenSpec workflow
- **WHEN** an issue starts with `mo propose` or `mo issue start`
- **AND** the system detects no existing Change (or creates new version)
- **THEN** it follows the 4-stage workflow
- **AND** each stage has specific responsibilities

### Requirement: Plan stage behavior
The plan stage SHALL generate Change artifacts and perform self-review.

#### Scenario: Generate Change artifacts
- **WHEN** plan stage executes
- **THEN** the agent:
  1. Explores codebase
  2. Creates `.mohist-specs/changes/{name}/`
  3. Writes proposal.md, design.md, specs/*.md
  4. Performs self-review (max 3 iterations)
  5. Generates prd.json if review passes
  6. Or pauses if max iterations reached

#### Scenario: Self-review iteration
- **WHEN** self-review iteration starts
- **THEN** agent validates:
  - All specs have clear AC
  - Design covers edge cases
  - Requirements are complete
- **AND** if issues found, agent fixes them
- **AND** if no improvement after 3 iterations, stage fails

### Requirement: Review stage behavior
The review stage SHALL be an approval gate for human review.

#### Scenario: Human review Change
- **WHEN** review stage executes
- **THEN** the system presents Change artifacts to user
- **AND** user can:
  - Edit any file (proposal.md, design.md, specs/*.md, prd.json)
  - Add comments to issue
  - Approve to proceed to build
  - Or go back to plan

### Requirement: Build stage behavior
The build stage SHALL execute Ralph-style task loop.

#### Scenario: Ralph loop execution
- **WHEN** build stage executes
- **THEN** main-agent:
  1. Reads prd.json and task-status.json
  2. For each pending task:
     - Assembles context (proposal + design + spec + learnings)
     - Calls spawn_coder
     - Verifies AC
     - Stores learning
     - Updates task-status.json
  3. Continues until all tasks complete or failure

### Requirement: Check stage behavior
The check stage SHALL perform automated testing and human acceptance.

#### Scenario: Automated testing
- **WHEN** check stage starts
- **THEN** agent automatically runs:
  - `npm test` (or equivalent)
  - `npm run lint` (or equivalent)
  - Any other validation commands from workflow config
- **AND** reports results in issue comment

#### Scenario: Human acceptance
- **WHEN** automated tests pass
- **THEN** system waits for human approval (approval gate)
- **AND** user can:
  - Review all changes
  - Approve to complete
  - Or request fixes (loop back to build)

#### Scenario: Archive Change
- **WHEN** check stage completes with approval
- **THEN** system moves Change to `.mohist-specs/archive/YYYY-MM-DD-{name}/`
- **AND** marks issue as done

### Requirement: Backward compatibility
The system SHALL support traditional workflow for issues without Change artifacts.

#### Scenario: Traditional workflow
- **WHEN** an issue has no `.mohist-specs/changes/` directory
- **THEN** it follows the traditional 3-stage workflow:
  - plan (temporary output)
  - build (single spawn_coder)
  - check (validation)
- **AND** no Change artifacts are created
