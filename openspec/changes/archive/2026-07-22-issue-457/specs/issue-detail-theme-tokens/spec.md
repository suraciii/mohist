### Requirement: Stateful container blocks use semantic theme tokens
The branch bar and the per-task execution/progress log surface on the issue detail page MUST use semantic theme tokens for all state-bearing foreground, background, and border colors on their container chrome. They MUST NOT use literal light-theme-tinted palette utilities (amber/blue/red/green/gray/purple/slate tints). Each listed block MUST render with theme-appropriate colors in both light and dark themes. The scope is limited to these listed stateful blocks; the execution log's deliberate dark console surface is preserved, not tokenized away.

#### Scenario: Branch bar behind state renders with warning tokens in dark theme
- **WHEN** the working branch is behind its base branch and the active theme is dark
- **THEN** the branch bar MUST render its behind state using the semantic warning token family and MUST NOT apply literal amber/blue palette utilities

#### Scenario: Branch bar rebasing state renders with info tokens in dark theme
- **WHEN** a rebase is in progress and the active theme is dark
- **THEN** the branch bar MUST render the rebasing state using the semantic info token family and MUST NOT apply literal blue palette utilities

#### Scenario: Branch bar upstream-unknown state renders with muted tokens in dark theme
- **WHEN** upstream status is unknown and the active theme is dark
- **THEN** the branch bar MUST render the upstream-unknown state using the semantic muted token family and MUST NOT apply literal gray palette utilities

#### Scenario: Branch bar error and conflict blocks render with the destructive token in dark theme
- **WHEN** the branch bar shows a rebase error or conflicting files and the active theme is dark
- **THEN** the error/conflict block MUST render using the destructive (or danger) token rather than a literal red palette utility

#### Scenario: Task execution/progress log chrome renders with semantic tokens in dark theme
- **WHEN** the per-task execution/progress log is open and the active theme is dark
- **THEN** the log panel chrome — the panel background and border, the search control, the source-filter chips, and the amber truncation badge — MUST render using semantic theme tokens and MUST NOT use literal light-theme-tinted palette utilities

#### Scenario: The execution log deliberate dark console surface is preserved
- **WHEN** the per-task execution/progress log renders its log lines
- **THEN** the deliberate dark console surface (the dark terminal background and its light foreground line colors) MUST be preserved unchanged; it MUST NOT be replaced with semantic theme tokens
