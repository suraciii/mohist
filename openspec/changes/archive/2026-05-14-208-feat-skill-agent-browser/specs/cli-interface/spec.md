## MODIFIED Requirements

### Requirement: CLI provides shared agent skill management

The CLI SHALL provide local commands that install Mohist-provided coder skill discovery stubs and read version-matched built-in skill content without requiring the Mohist server.

#### Scenario: Install shared agent skill stubs

- **WHEN** the user runs `mo skills install`
- **THEN** the CLI writes `.agents/skills/mohist/SKILL.md`
- **AND** the CLI writes `.agents/skills/mohist-explore/SKILL.md`
- **AND** each installed file is a lightweight discovery stub rather than the full packaged guidance
- **AND** each installed `SKILL.md` includes `name`, `description`, and `hidden: true` frontmatter

#### Scenario: Install to explicit path

- **WHEN** the user runs `mo skills install --path <repo>`
- **THEN** the CLI writes shared skill stubs under `<repo>/.agents/skills`
- **AND** the CLI does not write shared skill stubs under the current working directory unless it is the selected path

#### Scenario: Existing user-authored skills remain untouched

- **WHEN** the user runs `mo skills install`
- **THEN** the CLI manages only the Mohist-provided built-in skill names
- **AND** does not create, overwrite, delete, or scan unrelated user-authored skill directories such as `.agents/skills/mohist-po/`

#### Scenario: Internal Mohist skills are untouched

- **WHEN** the user runs `mo skills install`, `mo skills list`, `mo skills get`, or `mo skills path`
- **THEN** the CLI does not create, update, delete, or scan `.mohist/skills`
- **AND** `SkillService` behavior is unchanged

### Requirement: CLI serves packaged built-in skill content

The CLI SHALL resolve Mohist-provided built-in skill content from packaged skill assets so `mo skills` always serves content that matches the running CLI version.

#### Scenario: List visible built-in skills

- **WHEN** the user runs `mo skills list`
- **THEN** the CLI lists non-hidden built-in Mohist skills sorted by name
- **AND** hidden discovery stubs are not shown as duplicate list entries

#### Scenario: List visible built-in skills as JSON

- **WHEN** the user runs `mo skills list --json`
- **THEN** the CLI returns JSON entries for the visible built-in skills
- **AND** each entry includes the skill name and description

#### Scenario: Get built-in skill content

- **WHEN** the user runs `mo skills get mohist`
- **THEN** the CLI prints the packaged full `mohist` skill guidance
- **AND** the output matches the current built-in skill-data content rather than any repository-installed stub

#### Scenario: Get built-in skill content with supplementary files

- **WHEN** the user runs `mo skills get mohist --full`
- **THEN** the CLI prints the packaged full `mohist` skill guidance
- **AND** appends supplementary files from packaged `references/` and `templates/` directories in deterministic sorted order

#### Scenario: Get all built-in skills

- **WHEN** the user runs `mo skills get --all`
- **THEN** the CLI returns the visible built-in Mohist skill set backed by packaged full content

#### Scenario: Resolve built-in skill path

- **WHEN** the user runs `mo skills path mohist`
- **THEN** the CLI prints the packaged directory path for the built-in `mohist` skill data

#### Scenario: Override packaged skill root for development and tests

- **WHEN** the environment sets `MOHIST_SKILLS_DIR` to a valid built-in skill asset root
- **THEN** `mo skills list`, `get`, and `path` resolve skills from that override path instead of the default packaged lookup
