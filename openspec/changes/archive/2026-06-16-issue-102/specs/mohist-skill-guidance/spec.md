## ADDED Requirements

### Requirement: Explore skill guidance includes frontmatter production workflow

The shared `mohist-explore` skill guidance SHALL instruct the agent to produce issue body files with YAML frontmatter containing `recommended_workflow`, `recommended_workflow_reason`, and `risk`, followed by structured sections (Background, Goal, Non-goals, Acceptance criteria).

#### Scenario: Skill documents frontmatter format

- **WHEN** an agent reads the explore skill guidance
- **THEN** the guidance SHALL document the expected frontmatter fields: `recommended_workflow`, `recommended_workflow_reason`, and `risk`
- **AND** provide a template showing the frontmatter block and structured body sections

#### Scenario: Skill documents file-backed issue creation workflow

- **WHEN** the skill instructs the agent to create an issue after exploration
- **THEN** the guidance SHALL instruct the agent to write the body to a file
- **AND** call `mo issue create <title> --body-file <file>` to create the issue
- **AND** explain that the CLI will parse the frontmatter automatically

### Requirement: Explore skill guidance includes workflow discovery

The shared `mohist-explore` skill guidance SHALL instruct the agent to discover available workflows by calling `mo workflow list --described` before recommending one.

#### Scenario: Skill documents workflow discovery command

- **WHEN** an agent reads the explore skill guidance
- **THEN** the guidance SHALL document the `mo workflow list --described` command
- **AND** explain how to interpret workflow IDs, descriptions, and `suitable_for` metadata

#### Scenario: Skill documents matching logic

- **WHEN** the agent has obtained the workflow list
- **THEN** the guidance SHALL instruct the agent to match exploration context against `suitable_for` descriptions
- **AND** recommend the best-fit workflow with a reason derived from the workflow's description or suitability metadata

#### Scenario: Skill documents default fallback

- **WHEN** no workflow profile matches the exploration findings
- **THEN** the guidance SHALL instruct the agent to default to `mohist/default`
- **AND** include a reason stating that no specific workflow matched

### Requirement: Explore skill guidance includes user confirmation step

The shared `mohist-explore` skill guidance SHALL instruct the agent to present the workflow recommendation, risk assessment, and body summary to the user before creating the issue.

#### Scenario: Skill documents confirmation flow

- **WHEN** the agent has produced the body file with frontmatter
- **THEN** the guidance SHALL instruct the agent to display the recommended workflow, risk, and a body summary to the user
- **AND** wait for user confirmation before invoking `mo issue create --body-file <file>`

#### Scenario: Skill documents user override flow

- **WHEN** the user wants to change the recommendation
- **THEN** the guidance SHALL instruct the agent to allow modification of frontmatter values before creation
