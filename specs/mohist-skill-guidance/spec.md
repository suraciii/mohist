## MODIFIED Requirements

### Requirement: Mohist skill guidance is served from version-matched packaged content

The shared `mohist` and `mohist-explore` coder skill guidance SHALL be served from Mohist-packaged skill data so built-in guidance stays aligned with the installed CLI version instead of drifting in repository-local copies. Repository and Claude installs SHALL use lightweight discovery stubs that direct agents to `mo skills get <name>`, while Hermes installs SHALL copy full packaged skill data.

#### Scenario: OpenCode install writes discovery stubs

- **WHEN** the user runs `mo skills install`
- **THEN** Mohist installs `mohist` and `mohist-explore` under `.agents/skills/`
- **AND** each installed `SKILL.md` is a discovery stub with valid AgentSkills frontmatter
- **AND** each installed stub tells agents to run `mo skills get <name>` for full version-matched guidance

#### Scenario: Claude install writes discovery stubs

- **WHEN** the user runs `mo skills install --claude`
- **THEN** Mohist installs `mohist` and `mohist-explore` under `.claude/skills/`
- **AND** each installed `SKILL.md` is a discovery stub with valid AgentSkills frontmatter
- **AND** each installed stub tells agents to run `mo skills get <name>` for full version-matched guidance

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

#### Scenario: Existing repository and Claude installs remain discovery stubs

- **WHEN** the user runs `mo skills install` without `--hermes`
- **THEN** Mohist keeps installing discovery stubs to `.agents/skills/`
- **WHEN** the user runs `mo skills install --claude`
- **THEN** Mohist keeps installing discovery stubs to `.claude/skills/`
- **AND** neither mode writes to the Hermes skills directory

### Requirement: Mohist skill guidance documentation matches restored skills commands

The README and shipped Mohist skill stubs SHALL document the restored `mo skills install`, `mo skills list`, `mo skills get`, and `mo skills path` command surface. Documentation and stubs SHALL NOT instruct users to run `mo skills update`; refreshing installed Mohist-managed skills SHALL be documented as rerunning `mo skills install`.

#### Scenario: README documents install refresh workflow

- **WHEN** the user reads the README section for coder agent skills
- **THEN** it documents `mo skills install` as the command for both initial install and refresh
- **AND** it does not document `mo skills update` unless that command is explicitly implemented as an alias

#### Scenario: Shipped stubs point to get command

- **WHEN** a user or agent reads the shipped `mohist` or `mohist-explore` discovery stub
- **THEN** the stub points to `mo skills get <name>` for full guidance
- **AND** it does not depend on repository-local full skill copies being manually maintained
