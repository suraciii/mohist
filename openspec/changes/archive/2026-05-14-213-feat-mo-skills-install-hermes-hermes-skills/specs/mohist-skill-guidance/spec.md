## MODIFIED Requirements

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
