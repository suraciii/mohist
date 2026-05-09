## ADDED Requirements

### Requirement: CLI provides shared agent skill management

The CLI SHALL provide local commands that install and update Mohist-provided coder agent skills under `.agents/skills` without requiring the Mohist server.

#### Scenario: Install shared agent skills
- **WHEN** the user runs `mo skills install`
- **THEN** the CLI writes `.agents/skills/mohist/SKILL.md`
- **AND** the CLI writes `.agents/skills/mohist-explore/SKILL.md`
- **AND** each generated skill directory name matches its frontmatter `name`
- **AND** each generated `SKILL.md` includes `name` and `description` frontmatter
- **AND** the CLI does not write `.agents/skills/mohist-walkthrough/SKILL.md`

#### Scenario: Install to explicit path
- **WHEN** the user runs `mo skills install --path <repo>`
- **THEN** the CLI writes shared agent skills under `<repo>/.agents/skills`
- **AND** the CLI does not write shared agent skills under the current working directory unless it is the selected path

#### Scenario: Re-run generated install or update
- **WHEN** the shared agent skill files already exist and are recognized as Mohist-generated
- **AND** the user runs `mo skills install` or `mo skills update`
- **THEN** the CLI refreshes stale generated files from the built-in templates
- **AND** leaves current generated files unchanged
- **AND** the command completes successfully

#### Scenario: Update repairs partial generated installation
- **WHEN** one distributed shared agent skill is missing from `.agents/skills`
- **AND** the user runs `mo skills update`
- **THEN** the CLI creates the missing `mohist` or `mohist-explore` skill from the built-in template
- **AND** continues to protect existing user-modified files

#### Scenario: Protect user-modified skill files
- **WHEN** a target skill file exists but is not recognized as an unchanged Mohist-generated file
- **AND** the user runs `mo skills install` or `mo skills update`
- **THEN** the CLI does not overwrite that file
- **AND** the CLI reports the file as skipped or protected

#### Scenario: Force overwrite protected skill files
- **WHEN** a target skill file exists but is not recognized as an unchanged Mohist-generated file
- **AND** the user runs `mo skills install --force`
- **THEN** the CLI overwrites that file with the current built-in template
- **AND** the resulting file is recognized as Mohist-generated

#### Scenario: Help distinguishes skill types
- **WHEN** the user views `mo skills --help`, `mo skills install --help`, or `mo skills update --help`
- **THEN** the help text describes `.agents/skills` as coder agent skills
- **AND** distinguishes them from Mohist internal `.mohist/skills`
- **AND** does not imply that these commands execute internal Mohist skills

#### Scenario: Internal Mohist skills are untouched
- **WHEN** the user runs `mo skills install` or `mo skills update`
- **THEN** the CLI does not create, update, delete, or scan `.mohist/skills`
- **AND** `SkillService` behavior is unchanged
