## MODIFIED Requirements

### Requirement: CLI provides shared agent skill management

The CLI SHALL provide a `mo skills` command group for local coder-agent skill management. The command group SHALL explain in help output that it manages coder agent skills, SHALL expose `install`, `list`, `get`, and `path` subcommands, SHALL NOT expose `update`, and SHALL execute without requiring the Mohist server to be running.

#### Scenario: Skills command group is registered

- **WHEN** the user runs `mo skills --help`
- **THEN** the CLI displays help for managing coder agent skills
- **AND** the help lists `install`, `list`, `get`, and `path`
- **AND** the help does not list `update`

#### Scenario: Install shared agent skill stubs

- **WHEN** the user runs `mo skills install`
- **THEN** the CLI writes `.agents/skills/mohist/SKILL.md`
- **AND** the CLI writes `.agents/skills/mohist-explore/SKILL.md`
- **AND** each installed file is a lightweight discovery stub rather than the full packaged guidance
- **AND** each installed `SKILL.md` includes valid AgentSkills frontmatter with matching `name` and `description`
- **AND** each installed stub points users or agents to `mo skills get <name>` for full version-matched guidance

#### Scenario: Install overwrites Mohist-managed stubs

- **WHEN** the user runs `mo skills install` after an older Mohist-provided `mohist` or `mohist-explore` stub already exists
- **THEN** the CLI overwrites or refreshes that built-in skill target with the current Mohist-managed stub content

#### Scenario: Install to explicit path

- **WHEN** the user runs `mo skills install --path <repo>`
- **THEN** the CLI writes shared skill stubs under `<repo>/.agents/skills`
- **AND** the CLI does not write shared skill stubs under the current working directory unless it is the selected path

#### Scenario: Install Claude skill stubs

- **WHEN** the user runs `mo skills install --claude`
- **THEN** the CLI writes `.claude/skills/mohist/SKILL.md`
- **AND** the CLI writes `.claude/skills/mohist-explore/SKILL.md`
- **AND** the CLI does not write `.agents/skills/mohist/SKILL.md` or `.agents/skills/mohist-explore/SKILL.md` unless that is also the selected install target from a separate command

#### Scenario: Install Hermes packaged skill data

- **WHEN** the user runs `mo skills install --hermes`
- **THEN** the CLI writes full packaged `mohist` and `mohist-explore` skill directories under `${HERMES_HOME:-~/.hermes}/skills/`
- **AND** installed Hermes skills are usable without first running `mo skills get <name>`
- **AND** the CLI does not install user-created skills such as `mohist-po` unless they are intentionally added to the built-in skill set

#### Scenario: Hermes install rejects incompatible options

- **WHEN** the user runs `mo skills install --hermes --path <repo>` or `mo skills install --hermes --claude`
- **THEN** the CLI rejects the command with a clear validation error
- **AND** the CLI does not write any skill files

#### Scenario: Existing user-authored skills remain untouched

- **WHEN** the user runs `mo skills install`
- **THEN** the CLI manages only the Mohist-provided built-in skill names
- **AND** does not create, overwrite, delete, or scan unrelated user-authored skill directories such as `.agents/skills/mohist-po/`

#### Scenario: Internal Mohist skills are untouched

- **WHEN** the user runs `mo skills install`, `mo skills list`, `mo skills get`, or `mo skills path`
- **THEN** the CLI does not create, update, delete, or scan `.mohist/skills`
- **AND** `SkillService` behavior is unchanged

#### Scenario: Skills commands run without server

- **WHEN** the user runs `mo skills install`, `mo skills list`, `mo skills get`, or `mo skills path`
- **AND** the Mohist server is not running
- **THEN** the command completes according to its local filesystem behavior without performing a server availability check

### Requirement: CLI serves packaged built-in skill content

The CLI SHALL resolve Mohist-provided built-in skill content from packaged skill assets so `mo skills` always serves content that matches the running CLI version. The visible built-in skill set SHALL include at least `mohist` and `mohist-explore` and SHALL be sorted by name in list and all-skill output.

#### Scenario: List visible built-in skills

- **WHEN** the user runs `mo skills list`
- **THEN** the CLI lists visible built-in Mohist skills sorted by name
- **AND** the output includes `mohist` and `mohist-explore`
- **AND** hidden discovery stubs are not shown as duplicate list entries

#### Scenario: List visible built-in skills as JSON

- **WHEN** the user runs `mo skills list --json`
- **THEN** the CLI returns structured JSON entries for the visible built-in skills
- **AND** each entry includes the skill name and description
- **AND** entries are sorted by skill name

#### Scenario: Get built-in skill content

- **WHEN** the user runs `mo skills get mohist`
- **THEN** the CLI prints the packaged full `mohist` skill guidance
- **AND** the output matches the current built-in skill-data content rather than any repository-installed stub

#### Scenario: Get built-in skill content as JSON

- **WHEN** the user runs `mo skills get mohist --json`
- **THEN** the CLI returns structured JSON for the packaged `mohist` skill
- **AND** the JSON includes at least the skill name, description, and full guidance content

#### Scenario: Get built-in skill content with supplementary files

- **WHEN** the user runs `mo skills get mohist --full`
- **THEN** the CLI prints the packaged full `mohist` skill guidance
- **AND** appends supplementary files from packaged `references/` and `templates/` directories in deterministic sorted order

#### Scenario: Get all built-in skills

- **WHEN** the user runs `mo skills get --all`
- **THEN** the CLI returns all visible built-in Mohist skills backed by packaged full content
- **AND** the skills are emitted in deterministic name order

#### Scenario: Resolve built-in skill path

- **WHEN** the user runs `mo skills path mohist`
- **THEN** the CLI prints the packaged directory path for the built-in `mohist` skill data

#### Scenario: Resolve built-in skill path as JSON

- **WHEN** the user runs `mo skills path mohist --json`
- **THEN** the CLI returns structured JSON that includes the skill name and packaged directory path

#### Scenario: Unknown built-in skill fails clearly

- **WHEN** the user runs `mo skills get unknown-skill` or `mo skills path unknown-skill`
- **THEN** the CLI prints a clear unknown skill error
- **AND** exits with a non-zero status

#### Scenario: Override packaged skill root for development and tests

- **WHEN** the environment sets `MOHIST_SKILLS_DIR` to a valid built-in skill asset root
- **THEN** `mo skills list`, `get`, and `path` resolve skills from that override path instead of the default packaged lookup
