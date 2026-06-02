## MODIFIED Requirements

### Requirement: CLI provides shared agent skill management

The CLI SHALL provide local commands that install Mohist-provided coder skill discovery stubs and read version-matched built-in skill content without requiring the Mohist server. Packaged CLI skill assets SHALL be resolved from `MOHIST_SKILLS_DIR` when valid, otherwise from the version-compatible managed cache at `~/.mohist/cli/skill-data`, otherwise from sibling publish or development assets at `AppContext.BaseDirectory/skill-data`. The CLI SHALL NOT use runtime/internal `.mohist/skills` as a packaged coder-agent asset store.

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

#### Scenario: Discovery stubs load managed packaged guidance

- **WHEN** installed discovery stubs instruct an agent to run `mo skills get mohist` or `mo skills get mohist-explore`
- **AND** the managed asset cache at `~/.mohist/cli/skill-data` is present and version-compatible
- **THEN** each command returns the corresponding full packaged guidance without requiring `MOHIST_SKILLS_DIR`

### Requirement: CLI serves packaged built-in skill content

The CLI SHALL resolve Mohist-provided built-in skill content from packaged skill assets so `mo skills` always serves content that matches the running CLI version. The default packaged asset root SHALL be the managed cache at `~/.mohist/cli/skill-data` when it exists and is version-compatible. `MOHIST_SKILLS_DIR` SHALL remain the highest-precedence development and test override, and `AppContext.BaseDirectory/skill-data` SHALL remain a publish and development fallback.

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
- **AND** the managed asset cache is in use
- **THEN** the CLI prints the managed packaged directory path under `~/.mohist/cli/skill-data/mohist`

#### Scenario: Override packaged skill root for development and tests

- **WHEN** the environment sets `MOHIST_SKILLS_DIR` to a valid built-in skill asset root
- **THEN** `mo skills list`, `get`, and `path` resolve skills from that override path instead of the managed cache or sibling packaged lookup

#### Scenario: Fallback to sibling packaged asset root

- **WHEN** `MOHIST_SKILLS_DIR` is not set
- **AND** `~/.mohist/cli/skill-data` is absent
- **AND** `AppContext.BaseDirectory/skill-data` contains compatible packaged skill assets
- **THEN** `mo skills list`, `mo skills get`, and `mo skills path` resolve from `AppContext.BaseDirectory/skill-data`

#### Scenario: Missing or incompatible assets fail with repair guidance

- **WHEN** no valid packaged asset root can provide a requested built-in skill compatible with the running CLI
- **THEN** `mo skills get <name>` fails with a clear diagnostic that identifies the missing, stale, or incompatible asset state
- **AND** the diagnostic tells the user to repair the local installation by rerunning `mo update` or `scripts/install-mo.sh`
- **AND** the diagnostic does not only report a missing `SKILL.md` file path

## ADDED Requirements

### Requirement: CLI install and update synchronize packaged skill assets

`mo update` and `scripts/install-mo.sh` SHALL install the bare `mo` executable and synchronize Mohist-packaged built-in skill assets from the publish output into the managed cache at `~/.mohist/cli/skill-data`. Synchronization SHALL replace Mohist-managed built-in skill assets atomically enough that users do not observe a half-updated managed asset directory.

#### Scenario: mo update synchronizes managed skill assets

- **WHEN** the user runs `mo update`
- **AND** the CLI publish output contains `skill-data/mohist/SKILL.md` and `skill-data/mohist-explore/SKILL.md`
- **THEN** Mohist installs the updated `mo` binary
- **AND** synchronizes the publish output `skill-data` directory into `~/.mohist/cli/skill-data`
- **AND** subsequent `mo skills get mohist` and `mo skills get mohist-explore` work without `MOHIST_SKILLS_DIR`

#### Scenario: Manual install synchronizes managed skill assets

- **WHEN** the user runs `scripts/install-mo.sh`
- **AND** the publish output contains packaged skill assets
- **THEN** the script installs the `mo` binary
- **AND** synchronizes packaged skill assets into `~/.mohist/cli/skill-data`
- **AND** subsequent `mo skills path mohist` reports the managed asset path when the managed cache is in use

#### Scenario: Managed asset cache includes manifest

- **WHEN** `mo update` or `scripts/install-mo.sh` synchronizes packaged skill assets
- **THEN** `~/.mohist/cli/skill-data/manifest.json` records the CLI build identity, including at least version or git hash
- **AND** the manifest records the names of bundled built-in skills

#### Scenario: Synchronization replaces Mohist-managed assets only

- **WHEN** `mo update` or `scripts/install-mo.sh` refreshes packaged skill assets
- **THEN** Mohist replaces the managed built-in skill asset cache under `~/.mohist/cli/skill-data`
- **AND** it does not modify user-authored or external agent skills
- **AND** it does not read, write, or mutate runtime/internal `.mohist/skills`
