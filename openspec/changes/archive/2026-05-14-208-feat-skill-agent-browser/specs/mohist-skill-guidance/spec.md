## MODIFIED Requirements

### Requirement: Mohist skill guidance is served from version-matched packaged content

The shared `mohist` and `mohist-explore` coder skill guidance SHALL be served from Mohist-packaged skill data so built-in guidance stays aligned with the installed CLI version instead of drifting in repository-local copies.

#### Scenario: Installed stub points to packaged guidance

- **WHEN** Mohist installs the shared `mohist` or `mohist-explore` coder skill into a repository
- **THEN** the installed `SKILL.md` is a discovery stub
- **AND** it instructs the user or agent to use `mo skills get <name>` to retrieve full guidance

#### Scenario: Built-in guidance updates without reinstalling full payloads

- **WHEN** the Mohist CLI version changes and built-in packaged skill guidance changes with it
- **THEN** `mo skills get <name>` returns the new packaged guidance immediately
- **AND** the repository does not need a copied full `SKILL.md` payload to be refreshed first

#### Scenario: Full guidance can include supplementary reference material on demand

- **WHEN** the user runs `mo skills get mohist --full`
- **THEN** the CLI returns the base packaged skill guidance plus supplementary reference content
- **AND** the base repository-installed stub remains compact
