## ADDED Requirements

### Requirement: Application-level settings tabs are routed at the application scope, outside the project route scope

The Settings shell SHALL route the application-level tabs — Coder Agent (`ai`), Runtime (`agent`), System (`system`), and Preferences (`preferences`) — at `/settings/<section>`, outside the `ProjectRouteScope`. These routes SHALL NOT carry a project-name segment and SHALL NOT depend on a selected project.

#### Scenario: Application-level tabs resolve at /settings/<section>

- **WHEN** the browser navigates to `/settings/ai`, `/settings/agent`, `/settings/system`, or `/settings/preferences`
- **THEN** the corresponding application-level Settings section SHALL render
- **AND** the active URL SHALL NOT contain a project-name segment

#### Scenario: Application-level sections are no longer resolved under the project route scope

- **WHEN** the Settings shell resolves a route for an application-level section
- **THEN** the route SHALL be evaluated outside the `ProjectRouteScope`
- **AND** the application-level section SHALL render without a selected-project requirement

### Requirement: Project-level settings tabs remain routed under the project scope

The Settings shell SHALL route the project-level tabs — Repositories (`repositories`), Templates (`templates`), Label catalog (`label-catalog`), Workflows (`workflows`), and Inbox (`inbox`) — at `/:projectName/settings/<section>`, inside the `ProjectRouteScope`. Project-level settings SHALL continue to require a selected project.

#### Scenario: Project-level tabs resolve under /:projectName/settings/<section>

- **WHEN** the browser navigates to `/:projectName/settings/repositories`, `/:projectName/settings/templates`, `/:projectName/settings/label-catalog`, `/:projectName/settings/workflows`, or `/:projectName/settings/inbox`
- **THEN** the corresponding project-level Settings section SHALL render scoped to that project
- **AND** the active URL SHALL carry the project-name segment

#### Scenario: Project-level sections are not served at the application scope

- **WHEN** the browser navigates to an application-scope URL for a project-level section (e.g. `/settings/repositories`)
- **THEN** the Settings shell SHALL NOT render that project-level section as application-level config
- **AND** the project-level section SHALL remain subject to the project scope (requiring a selected project)

### Requirement: Global settings remain reachable when no project exists

The Settings shell SHALL keep the application-level settings tabs reachable when the system contains no project. The project-existence gate that otherwise presents a "No projects yet" prompt SHALL NOT apply to the `/settings/*` tree.

#### Scenario: Global settings are reachable with zero projects

- **WHEN** the system has no project
- **AND** the browser navigates to an application-level settings route at `/settings/<section>`
- **THEN** the application-level Settings section SHALL render
- **AND** the "No projects yet" project-creation prompt SHALL NOT be presented for the `/settings/*` tree

### Requirement: Legacy project-scoped deep links to global sections redirect to the application scope

To preserve existing bookmarks and in-app deep links, the Settings shell SHALL redirect a legacy project-scoped global-section URL (`/:projectName/settings/<global-section>`) to the application-scoped URL (`/settings/<global-section>`) for each global section (`ai`, `agent`, `system`, `preferences`). The redirect SHALL be a replacement navigation so the legacy URL does not linger in history.

#### Scenario: Legacy global-section project URL redirects to the application scope

- **WHEN** the browser navigates to `/:projectName/settings/ai`
- **THEN** the Settings shell SHALL redirect to `/settings/ai`
- **AND** the Coder Agent section SHALL render at `/settings/ai`

#### Scenario: Project-section project URLs are not redirected

- **WHEN** the browser navigates to `/:projectName/settings/repositories`
- **THEN** the Settings shell SHALL NOT redirect to `/settings/repositories`
- **AND** the Repositories section SHALL render at `/:projectName/settings/repositories`

### Requirement: Settings navigation is a left sub-navigation grouped into Application and Project scopes

The Settings shell SHALL present navigation as a left sub-navigation (replacing the top horizontal tab bar), grouped into two visually distinct groups: **Application** — containing Coder Agent, Runtime, System, and Preferences; and **Project** — containing Repositories, Templates, Label catalog, Workflows, and Inbox. The group boundary SHALL make each setting's scope (application vs project) visually distinguishable. Selecting an Application item SHALL navigate to `/settings/<section>`; selecting a Project item SHALL navigate to `/:projectName/settings/<section>`.

#### Scenario: Left sub-navigation renders grouped Application and Project items

- **WHEN** the Settings page renders
- **THEN** the navigation SHALL be a left sub-navigation (not a top horizontal tab bar)
- **AND** the navigation SHALL render an Application group containing Coder Agent, Runtime, System, and Preferences items
- **AND** the navigation SHALL render a Project group containing Repositories, Templates, Label catalog, Workflows, and Inbox items
- **AND** the Application and Project groups SHALL be visually distinguishable

#### Scenario: Selecting an Application item navigates to the application scope

- **WHEN** the user selects the Coder Agent navigation item
- **THEN** the browser SHALL navigate to `/settings/ai`
- **AND** the active URL SHALL NOT contain a project-name segment

#### Scenario: Selecting a Project item navigates to the project scope

- **WHEN** the user selects the Repositories navigation item
- **THEN** the browser SHALL navigate to `/:projectName/settings/repositories`
- **AND** the active URL SHALL contain the project-name segment

### Requirement: Section headings align with their navigation labels

Each Settings section's visible heading SHALL exactly match the label shown for that section's navigation item. Clarification copy that is not part of the heading SHALL be placed in the section's description rather than the navigation label.

#### Scenario: Heading equals navigation label for every section

- **WHEN** the user navigates to any Settings section
- **THEN** the section's visible heading SHALL equal that section's navigation-item label
- **AND** any clarification text SHALL appear in the section description, not in the navigation label

### Requirement: Settings sub-navigation surfaces an overflow affordance on narrow views

The Settings sub-navigation SHALL surface a visible overflow affordance (a gradient, arrow, or "more" cue) when its content overflows the available space on narrow views, instead of silently clipping navigation items.

#### Scenario: Overflow affordance appears when sub-navigation overflows

- **WHEN** the Settings sub-navigation content overflows the available space on a narrow viewport
- **THEN** a visible overflow affordance (gradient, arrow, or "more" cue) SHALL be surfaced
- **AND** navigation items SHALL NOT be silently clipped without any cue

### Requirement: Settings sub-navigation supports keyboard navigation with roving tabindex

The Settings sub-navigation SHALL support keyboard navigation using the arrow keys with a roving tabindex pattern, and SHALL set `aria-current="page"` on the active navigation item.

#### Scenario: Arrow keys move focus across navigation items

- **WHEN** focus is on a Settings navigation item
- **AND** the user presses an arrow key
- **THEN** focus SHALL move to an adjacent navigation item following the roving tabindex pattern

#### Scenario: Active navigation item reports aria-current page

- **WHEN** a Settings section is active
- **THEN** the corresponding navigation item SHALL carry `aria-current="page"`

### Requirement: Settings search navigates to the target section under its correct scope

The Settings search selection SHALL navigate to the chosen entry's section under that section's correct scope: application sections route to `/settings/<section>` and project sections route to `/:projectName/settings/<section>`. The search SHALL NOT route an application-level section through a project-scoped path.

#### Scenario: Selecting an application-level search result navigates to the application scope

- **WHEN** the user selects a Settings search result whose section is an application-level section (e.g. Coder Agent)
- **THEN** the browser SHALL navigate to `/settings/<section>`
- **AND** the active URL SHALL NOT contain a project-name segment

#### Scenario: Selecting a project-level search result navigates to the project scope

- **WHEN** the user selects a Settings search result whose section is a project-level section (e.g. Repositories)
- **THEN** the browser SHALL navigate to `/:projectName/settings/<section>`
- **AND** the active URL SHALL contain the project-name segment

### Requirement: The settings onboarding banner surface is removed

The Settings shell SHALL NOT render the onboarding banner. The `OnboardingBanner` component, its test, the `showOnboarding` state, the `ONBOARDING_DISMISSED_KEY` localStorage logic, and the onboarding render branch SHALL be removed. No code or reference to `OnboardingBanner` SHALL remain in the repository.

#### Scenario: Onboarding banner is not rendered

- **WHEN** the Settings page renders the Coder Agent section
- **THEN** no onboarding banner SHALL be rendered

#### Scenario: Onboarding banner code and references are absent

- **WHEN** the repository is inspected for onboarding artifacts
- **THEN** the `OnboardingBanner` component and its test SHALL NOT exist
- **AND** the `ONBOARDING_DISMISSED_KEY` localStorage logic SHALL NOT exist
- **AND** the `showOnboarding` state and onboarding render branch SHALL NOT exist
