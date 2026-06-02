## MODIFIED Requirements

### Requirement: Mohist skill guidance is served from version-matched packaged content

The shared `mohist` and `mohist-explore` coder skill guidance SHALL be served from Mohist-packaged skill data so built-in guidance stays aligned with the installed CLI version instead of drifting in repository-local copies. The CLI SHALL resolve packaged skill assets from `MOHIST_SKILLS_DIR` first when set and valid, then from the managed cache at `~/.mohist/cli/skill-data` when present and version-compatible with the running CLI, then from `AppContext.BaseDirectory/skill-data` as a publish and development fallback. Managed packaged skill assets SHALL remain separate from runtime/internal `.mohist/skills` state.

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

#### Scenario: Environment override takes precedence

- **WHEN** `MOHIST_SKILLS_DIR` is set to a valid built-in skill asset root
- **THEN** `mo skills list`, `mo skills get`, and `mo skills path` resolve packaged skill assets from that directory
- **AND** the CLI does not prefer `~/.mohist/cli/skill-data` or `AppContext.BaseDirectory/skill-data`

#### Scenario: Managed asset cache is preferred by default

- **WHEN** `MOHIST_SKILLS_DIR` is not set
- **AND** `~/.mohist/cli/skill-data` exists with a manifest compatible with the running CLI
- **THEN** `mo skills get mohist` prints the managed packaged `mohist` guidance
- **AND** `mo skills get mohist-explore` prints the managed packaged `mohist-explore` guidance
- **AND** `mo skills path mohist` prints the managed path under `~/.mohist/cli/skill-data/mohist`

#### Scenario: Sibling packaged asset fallback remains available

- **WHEN** `MOHIST_SKILLS_DIR` is not set
- **AND** the managed asset cache is absent
- **AND** `AppContext.BaseDirectory/skill-data` contains compatible packaged skill assets
- **THEN** `mo skills get mohist` resolves from `AppContext.BaseDirectory/skill-data`

#### Scenario: Managed asset mismatch is diagnosed clearly

- **WHEN** `MOHIST_SKILLS_DIR` is not set
- **AND** `~/.mohist/cli/skill-data` exists but its manifest is missing, stale, incompatible, or omits the requested built-in skill
- **THEN** `mo skills get <name>` fails with a clear version or asset mismatch diagnostic
- **AND** the diagnostic explains that the user can repair the installation by rerunning `mo update` or `scripts/install-mo.sh`
- **AND** the diagnostic does not report only that `SKILL.md` is missing

#### Scenario: Packaged asset management does not mutate runtime skills

- **WHEN** the user runs `mo skills get`, `mo skills path`, `mo skills list`, `mo update`, or `scripts/install-mo.sh`
- **THEN** Mohist does not read, create, update, delete, or scan runtime/internal `.mohist/skills` as part of packaged coder-agent skill asset management
