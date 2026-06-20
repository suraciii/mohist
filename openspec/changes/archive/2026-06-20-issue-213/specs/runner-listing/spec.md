## ADDED Requirements

### Requirement: Runners page is reachable from primary navigation

The Web App-Shell SHALL expose a `Runners` entry in primary navigation. Activating it SHALL navigate to the Runners page scoped to the current project. The page SHALL list every runner eligible for the current project, which includes both project-scoped runners and global-scope runners.

#### Scenario: Navigation entry present
- **WHEN** a user views primary navigation with a current project selected
- **THEN** a `Runners` entry SHALL be present alongside the other primary nav items
- **AND** activating it SHALL navigate to the project-scoped Runners route

#### Scenario: Entering shows current project runners including global
- **WHEN** a user enters the Runners page for a project that has project-scoped and global runners
- **THEN** the page SHALL display both the project-scoped and global runners in one list

#### Scenario: No current project does not query runners
- **WHEN** no project is currently selected and the Runners route is opened
- **THEN** the page SHALL NOT issue the project-scoped runner query
- **AND** the page SHALL render a non-runner state rather than a runner list

### Requirement: Runners page shows a status summary bar

The Runners page SHALL render a summary bar at the top of the list that counts runners in each status: `idle`, `busy`, `stale`, and `offline`. Counts SHALL reflect the runners currently shown after applying the active scope filter.

#### Scenario: Summary bar counts each status
- **WHEN** the Runners page renders a list containing idle, busy, stale, and offline runners
- **THEN** the summary bar SHALL show a count for each of `idle`, `busy`, `stale`, and `offline`

#### Scenario: Zero count is shown explicitly
- **WHEN** a status category has no runners
- **THEN** the summary bar SHALL show that category as zero rather than omitting it

#### Scenario: Summary follows scope filter
- **WHEN** the user applies a scope filter that excludes some runners
- **THEN** the summary bar counts SHALL update to reflect only the runners matching the filter

### Requirement: Runners page supports scope filtering

The Runners page SHALL provide a scope filter with three values: all (global + current project), global, and current project. The default selection SHALL be all.

#### Scenario: Default shows all scopes
- **WHEN** the Runners page loads without an explicit filter choice
- **THEN** the scope filter SHALL default to all
- **AND** both global and current-project runners SHALL be visible

#### Scenario: Global filter shows only global runners
- **WHEN** the user selects the global scope filter
- **THEN** only runners whose scope is global SHALL be shown

#### Scenario: Current-project filter shows only project runners
- **WHEN** the user selects the current-project scope filter
- **THEN** only runners scoped to the current project SHALL be shown

### Requirement: Runners page row displays the full runner field set

Each runner row SHALL display: runner id, kind, status badge, scope, capacity usage (used/total slots), heartbeat freshness (relative age since last heartbeat), and hostname. Offline and stale runners SHALL remain listed and SHALL be distinguished from healthy runners only by their status badge, not by being hidden or removed.

#### Scenario: Row renders all fields
- **WHEN** a runner row renders for a runner that has capacity and heartbeat data
- **THEN** the row SHALL show the runner id, kind, a status badge, scope, used/total capacity, heartbeat freshness, and hostname

#### Scenario: Offline and stale runners remain visible
- **WHEN** the list contains stale or offline runners
- **THEN** those runners SHALL remain in the list
- **AND** each SHALL carry a status badge distinguishing stale or offline from idle and busy

#### Scenario: Capacity unavailable does not break the row
- **WHEN** a runner has no capacity data (for example, an offline runner whose runtime state is unavailable)
- **THEN** the row SHALL render without crashing
- **AND** the row SHALL indicate capacity is unavailable rather than showing a default of zero used slots

### Requirement: Runners page shows an empty state with the start command

When no runners are eligible for the current project, the Runners page SHALL render an empty state that prompts the user with the runner start command.

#### Scenario: Empty state renders start hint
- **WHEN** the Runners page renders for a project with zero eligible runners
- **THEN** the page SHALL show an empty state containing the runner start command hint
- **AND** the page SHALL NOT render an empty table or a perpetual loading state

### Requirement: Runners page reflects live runner state

The Runners page SHALL refresh runner state on a bounded interval so status transitions (idle to busy, online to offline) appear without a manual page reload.

#### Scenario: Status transitions appear without reload
- **WHEN** a runner transitions from idle to busy or from online to offline while the page is open
- **THEN** the list and summary bar SHALL update to reflect the new status without requiring a manual reload

### Requirement: Activity page delegates runner listing to the Runners page

The Activity page SHALL NOT embed the runner list. It SHALL retain the runner overview badge in its status bar as a global quick indicator, and SHALL provide a link from the Activity page to the Runners page.

#### Scenario: Activity no longer embeds the runner list
- **WHEN** a user opens the Activity page
- **THEN** the page SHALL NOT render an embedded runner list section
- **AND** the page SHALL render only session-oriented content

#### Scenario: Activity links to the Runners page
- **WHEN** a user views the Activity page
- **THEN** the page SHALL present a link that navigates to the project-scoped Runners page

#### Scenario: Activity status-bar runner badge retained
- **WHEN** a user views the Activity page status bar
- **THEN** the runner overview badge SHALL remain present as a quick indicator of runner state

### Requirement: mo runner list subcommand outputs a runner table

The CLI SHALL provide a `mo runner list` subcommand under the existing `mo runner` command group. The subcommand SHALL print the current project's eligible runners as a table whose rows cover runner id, kind, status, scope, capacity, heartbeat freshness, and hostname.

#### Scenario: Subcommand appears in help
- **WHEN** a user runs `mo runner --help`
- **THEN** the output SHALL list `list` among the `mo runner` subcommands

#### Scenario: Table output renders columns
- **WHEN** a user runs `mo runner list` for a project with eligible runners
- **THEN** the command SHALL print a table whose rows include runner id, kind, status, scope, capacity, heartbeat freshness, and hostname

### Requirement: mo runner list color-codes runner status

The `mo runner list` subcommand SHALL distinguish runner status by terminal color so idle, busy, stale, and offline are visually separable in the table. Color codes and table borders SHALL be omitted when JSON output is selected.

#### Scenario: Status column is color-coded
- **WHEN** `mo runner list` prints runners with different statuses in table mode
- **THEN** the status value SHALL be rendered with a color that distinguishes idle, busy, stale, and offline from one another

#### Scenario: Color codes omitted in JSON output
- **WHEN** the user runs `mo runner list -o json`
- **THEN** the output SHALL be valid JSON without terminal color codes or table borders

### Requirement: mo runner list supports project and scope filters

The `mo runner list` subcommand SHALL accept a project override (`--project` / `--project-id`) and a `--scope` filter with values `all`, `global`, and `project`. When no project override is supplied, the current project context SHALL be used.

#### Scenario: Default uses current project and all scopes
- **WHEN** a user runs `mo runner list` without flags
- **THEN** the command SHALL query the current project
- **AND** SHALL list both global and project-scoped runners

#### Scenario: Scope filter restricts results
- **WHEN** a user runs `mo runner list --scope global`
- **THEN** the command SHALL list only global-scope runners

#### Scenario: Explicit project override
- **WHEN** a user runs `mo runner list --project <name>`
- **THEN** the command SHALL resolve the project via the same mechanism as other `mo` commands
- **AND** SHALL query that project's runners

### Requirement: mo runner list empty state shows the start command

When no runners are eligible for the queried project and scope, `mo runner list` SHALL print an empty state that prompts the user with the runner start command rather than an empty table.

#### Scenario: Empty state prints start hint
- **WHEN** a user runs `mo runner list` for a project with zero eligible runners
- **THEN** the command SHALL print the runner start command hint
- **AND** SHALL NOT print an empty table

### Requirement: mo runner list requires the server

The `mo runner list` subcommand SHALL require the Mohist server to be running and SHALL surface the standard server-unavailable error when it is not, consistent with other `mo` read subcommands.

#### Scenario: Server down surfaces standard error
- **WHEN** a user runs `mo runner list` and the server is not running
- **THEN** the CLI SHALL display the standard "Server is not running" message
- **AND** SHALL exit with a non-zero status

### Requirement: Runner listing uses a shared four-value status taxonomy

Both the Runners page and `mo runner list` SHALL present runner status using exactly the four values `idle`, `busy`, `stale`, and `offline`, as derived by the server. Neither surface SHALL invent additional status values nor collapse these four into fewer.

#### Scenario: Surfaces use the same status set
- **WHEN** the same runner list is rendered through the Runners page and `mo runner list`
- **THEN** both surfaces SHALL present status using only the `idle`, `busy`, `stale`, and `offline` values
