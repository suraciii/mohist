### Requirement: Stateful blocks use semantic theme tokens
The branch bar and the workflow status and task progress presentation on the issue detail page MUST use semantic theme tokens for all foreground, background, and border colors. They MUST NOT use literal palette utilities (amber/blue/red/green/gray tints). Each block MUST render with theme-appropriate colors in both light and dark themes.

#### Scenario: Branch bar behind state renders with semantic tokens in dark theme
- **WHEN** the working branch is behind its base branch and the active theme is dark
- **THEN** the branch bar MUST render its behind state using semantic theme tokens and MUST NOT apply literal amber/blue palette utilities

#### Scenario: Branch bar rebasing state renders with semantic tokens in dark theme
- **WHEN** a rebase is in progress and the active theme is dark
- **THEN** the branch bar MUST render the rebasing state using semantic theme tokens and MUST NOT apply literal blue palette utilities

#### Scenario: Branch bar upstream-unknown state renders with semantic tokens in dark theme
- **WHEN** upstream status is unknown and the active theme is dark
- **THEN** the branch bar MUST render the upstream-unknown state using semantic theme tokens and MUST NOT apply literal gray palette utilities

#### Scenario: Branch bar error and conflict blocks render with the destructive token in dark theme
- **WHEN** the branch bar shows a rebase error or conflicting files and the active theme is dark
- **THEN** the error/conflict block MUST render using the destructive (or equivalent semantic) token rather than a literal red palette utility

#### Scenario: Workflow status and task progress presentation renders with semantic tokens in dark theme
- **WHEN** workflow status and task progress presentation is visible and the active theme is dark
- **THEN** the presentation MUST render using semantic theme tokens and MUST NOT apply literal palette utilities, remaining legible in dark theme
