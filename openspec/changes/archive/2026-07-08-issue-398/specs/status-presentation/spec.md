### Requirement: Single shared mapping from domain state to semantic-token treatment

The system SHALL expose one shared status-presentation layer that maps every production domain state to exactly one semantic-token visual treatment, drawn from the `success`, `warning`, `info`, `danger`, and muted families. The mapping SHALL cover, at minimum: issue health (`active`, `paused`, `blocked`, `interrupted`, `cancelled`, `done`); workflow run status (`pending`, `ready`, `running`, `awaiting-approval`, `paused`, `completed`, `stopped`, `failed`, `created`, `unknown`); stage status (`running`, `awaiting-approval`, `completed`, `failed`, `interrupted`, and the not-started default); approval state; runner state (`idle`, `busy`, `stale`, `offline`); and event severity. Every status pill, badge, dot, and marker rendered on the covered surfaces SHALL resolve its visual treatment through this single layer rather than through a per-widget color map.

#### Scenario: Each domain state resolves to one treatment

- **WHEN** any covered surface renders a status for a given domain state
- **THEN** the surface SHALL obtain the treatment (background, foreground/text, border, dot, and icon class set) from the shared status-presentation layer
- **AND** SHALL NOT consult a widget-local color map (e.g. `STATUS_PILL_PAIRS`, `STATUS_CONFIG`, the per-widget presentation constants in `WorkflowRunStatusPill`, or the `statusBadge()` helper) to decide background, text, border, dot, or icon color

#### Scenario: New domain state falls back to the unknown treatment

- **WHEN** the layer is asked for a state it does not explicitly map (e.g. a newly added workflow status before the layer is updated)
- **THEN** it SHALL resolve to the reserved `unknown`/muted treatment
- **AND** SHALL NOT throw or render a colorless element

### Requirement: Status colors are reserved strictly for state meaning

The shared status-presentation layer SHALL reserve each semantic color family for a single meaning so that two states never share a color unless they share a meaning. The reserved mapping SHALL be: `running`→`info`; `awaiting-approval`, `blocked`, and `interrupted`→`warning` or `danger` (the specific family is fixed per state and applied uniformly); `drift`→`warning` or `danger` (fixed per state); `done`/`completed`→`success`; `failed`→`danger`; and `unknown`, `cancelled`, `paused` (health), and `offline`→muted. The `success` hue family SHALL be the only hue family used for the completed/done meaning.

#### Scenario: Completed and done share the success family exclusively

- **WHEN** a workflow run completes, a stage completes, an issue reaches `done`, or a runner is `idle` (healthy/available)
- **THEN** each SHALL render with the `success` family treatment
- **AND** SHALL NOT render with a `green` Tailwind palette class, an `emerald` Tailwind palette class, or any inline hex that is not the `--success*` token

#### Scenario: Running is distinguishable from awaiting approval

- **WHEN** a surface renders a `running` state and an `awaiting-approval` state side by side
- **THEN** the two SHALL be visually distinguishable through their reserved semantic families
- **AND** neither SHALL borrow the other's family

#### Scenario: No two unrelated states share a hue

- **WHEN** any two states with different meanings are rendered
- **THEN** they SHALL NOT resolve to the same semantic family unless their meanings are equivalent
- **AND** the `info` family SHALL NOT be reused to color a non-running state

### Requirement: Dot, pill, and marker treatments stay coherent within a state

For any single domain state, the background tint, foreground text, border, dot fill, and icon color SHALL all come from the same semantic family, so a status pill never pairs a `success` background with a `warning` dot or a `danger` border. The dot color SHALL be derivable from the same treatment as the pill text/background and SHALL NOT be set from a divergent widget-local source.

#### Scenario: Dot color tracks its pill family

- **WHEN** a status pill renders an inner dot for a given state
- **THEN** the dot fill SHALL resolve from the same semantic family as the pill's text/background treatment
- **AND** SHALL NOT use a raw Tailwind palette class (e.g. `bg-blue-700`, `bg-amber-700`) for that state

### Requirement: Existing divergent status color maps are collapsed into the shared layer

The divergent status-color helpers that exist today SHALL be removed or rewired so they no longer author status color decisions of their own. This SHALL include at minimum: the `statusBadge()` helper in `entities/issue/lib/status-badge.ts`; the per-status presentation constants in `widgets/issue-workflow/ui/WorkflowRunStatusPill.tsx`; the `StatusPill` branch-per-indicator styling and the `STATUS_PILL_PAIRS` hex map in `widgets/kanban-board/ui/IssueCard.tsx`; and the `STATUS_CONFIG` runner map in `widgets/runner-status/ui/RunnerList.tsx`. Each call site SHALL instead resolve its treatment from the shared status-presentation layer. Product terms (issue, workflow, stage, health, approval, runner, artifact, session, epic) SHALL be preserved unchanged.

#### Scenario: statusBadge delegates to the shared layer

- **WHEN** any code path renders an issue-health badge through the legacy `statusBadge()` entry point
- **THEN** the resulting class set SHALL be produced by the shared status-presentation layer for that health state
- **AND** SHALL NOT contain raw Tailwind palette classes such as `text-green-700`, `bg-amber-50`, `text-red-700`, or `text-orange-700`

#### Scenario: WorkflowRunStatusPill delegates to the shared layer

- **WHEN** a workflow-run status pill renders for any run status
- **THEN** its background, text, dot, and icon treatment SHALL come from the shared status-presentation layer
- **AND** the component SHALL NOT hold its own per-status `bg-*`/`text-*`/`dot` presentation constants
