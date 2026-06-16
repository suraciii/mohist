# OpenSpec Capability: mohist-skill-guidance

### Requirement: Mohist skill guidance recommends file-backed long issue bodies

The shared `mohist` skill guidance SHALL recommend file-backed or piped issue body workflows for long Markdown content instead of relying on shell-escaped inline strings as the primary path.

#### Scenario: Recommend file-backed issue body workflow
- **WHEN** the skill documents how to create an issue with a long Markdown body
- **THEN** it recommends `mo issue create "Title" --body @file.md` as the default workflow

#### Scenario: Recommend stdin workflow
- **WHEN** the skill documents pipeline-friendly issue creation
- **THEN** it documents `mo issue create "Title" --body -` as the stdin workflow

#### Scenario: Keep heredoc as fallback guidance
- **WHEN** the skill documents compatibility alternatives
- **THEN** it may mention heredoc or command substitution as fallback patterns
- **AND** it does not present shell-escaping long inline strings as the preferred workflow

### Requirement: Mohist skill guidance is served from version-matched packaged content

The shared `mohist` and `mohist-explore` coder skill guidance SHALL be served from Mohist-packaged skill data so built-in guidance stays aligned with the installed CLI version instead of drifting in repository-local copies.

#### Scenario: Hermes install copies full packaged guidance

- **WHEN** the user runs `mo skills install --hermes`
- **THEN** Mohist installs `mohist` and `mohist-explore` under `${HERMES_HOME:-~/.hermes}/skills/`
- **AND** each installed skill is copied from packaged `skill-data/<name>/`
- **AND** `mohist/references/issue-templates.md` is installed when present in packaged skill data

#### Scenario: Hermes install does not copy discovery stubs

- **WHEN** the user runs `mo skills install --hermes`
- **THEN** installed Hermes `SKILL.md` files contain the full packaged guidance
- **AND** they are not copied from `stubs/`
- **AND** they do not rely on first running `mo skills get <name>` to become useful in Hermes

#### Scenario: Hermes install is limited to Mohist built-in skills

- **WHEN** the user runs `mo skills install --hermes`
- **THEN** Mohist installs only the built-in `mohist` and `mohist-explore` skills
- **AND** user-defined skills such as `mohist-po` are not installed or modified

#### Scenario: Hermes install respects Hermes native home

- **WHEN** `HERMES_HOME` is set and the user runs `mo skills install --hermes`
- **THEN** Mohist installs under `$HERMES_HOME/skills/`
- **AND** it does not write to the user's default `~/.hermes/skills/`

#### Scenario: Hermes install leaves external dirs config untouched

- **WHEN** the user runs `mo skills install --hermes`
- **THEN** Mohist does not read or modify Hermes `config.yaml`
- **AND** Mohist does not add or change `skills.external_dirs`

#### Scenario: Hermes install reports repeatable results and usage

- **WHEN** the user runs `mo skills install --hermes` for a new Hermes skills directory
- **THEN** Mohist reports `created` for installed Mohist skills
- **WHEN** the user runs `mo skills install --hermes` again
- **THEN** Mohist updates the existing Mohist skill directories and reports `updated`
- **AND** the command output includes Hermes usage examples for `/mohist` and `/mohist-explore`
- **AND** the output notes that an existing Hermes session may need reload/reset or a new session before seeing the installed skills

#### Scenario: Existing repository and Claude installs remain unchanged

- **WHEN** the user runs `mo skills install` without `--hermes`
- **THEN** Mohist keeps installing discovery stubs to `.agents/skills/`
- **WHEN** the user runs `mo skills install --claude`
- **THEN** Mohist keeps installing discovery stubs to `.claude/skills/`
- **AND** neither mode writes to the Hermes skills directory


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
