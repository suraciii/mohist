### Requirement: Dashboard charts render through a single pinned chart library

The Dashboard SHALL render every chart through a single, project-wide pinned chart library. All dashboard chart surfaces — present and future — SHALL compose against this one library, so that chart rendering, theming, accessibility, and motion are handled uniformly. The Dashboard SHALL NOT introduce a second charting library alongside the pinned library; a future chart issue SHALL NOT add, swap, or wrap an alternative rendering library without retiring the previously pinned library.

#### Scenario: All dashboard charts use the one pinned library

- **WHEN** the Dashboard renders any chart surface
- **THEN** the chart SHALL be rendered by the single pinned chart library
- **AND** the chart SHALL NOT be rendered by a different or additional charting library

#### Scenario: A future chart reuses the pinned library rather than adding a new one

- **WHEN** a subsequent dashboard chart issue is implemented
- **THEN** the new chart SHALL compose against the already pinned chart library
- **AND** the implementation SHALL NOT introduce an additional charting library dependency

### Requirement: Chart colors are sourced from theme tokens, not hardcoded literals

Every color used on a dashboard chart surface — series fills, strokes, axes, grids, legends, and labels — SHALL be sourced from the application's theme token system (the same tokens that drive the rest of the UI). Chart surfaces SHALL NOT hardcode color literals (hex, rgb, hsl, or named CSS colors) for any chart element. This retire-on-the-chart-surface rule applies to all dashboard charts and supersedes the prior practice of hardcoding widget colors; the existing scalar `InvestmentPanel` figures are out of scope and retain their presentation.

#### Scenario: Chart series colors resolve to theme tokens

- **WHEN** a dashboard chart renders a data series (for example a bar fill or a line stroke)
- **THEN** the series color SHALL resolve to a theme token
- **AND** the chart component SHALL NOT embed a hardcoded color literal for that series

#### Scenario: Chart chrome colors resolve to theme tokens

- **WHEN** a dashboard chart renders axes, gridlines, or a legend
- **THEN** those elements SHALL be colored from theme tokens
- **AND** the chart surface SHALL NOT hardcode color literals for chrome

### Requirement: A shared three-state chart wrapper renders loading, error, and empty states

The Dashboard SHALL provide a single shared chart wrapper that renders the three non-content states — loading, error, and empty — so every dashboard chart presents those states consistently rather than each chart implementing its own. Every dashboard chart SHALL render its content through this wrapper, supplying only the resolved/loaded chart content; the wrapper SHALL own the loading, error, and empty presentations. The empty presentation rendered by the wrapper SHALL include a concrete next action that tells the operator how the chart will gain data (not a bare "no data"). The loading, error, and empty states SHALL be mutually exclusive with the resolved content.

#### Scenario: Every chart routes its non-content states through the wrapper

- **WHEN** a dashboard chart is in the loading, error, or empty state
- **THEN** the shared chart wrapper SHALL render that state
- **AND** the chart SHALL NOT implement its own bespoke loading, error, or empty presentation outside the wrapper

#### Scenario: Empty state names a concrete next action

- **WHEN** the shared chart wrapper renders the empty state for a dashboard chart
- **THEN** the wrapper SHALL display a concrete next action describing how the chart will gain data
- **AND** the empty state SHALL NOT be a bare "no data" message with no action

#### Scenario: States are mutually exclusive with resolved content

- **WHEN** a dashboard chart has resolved content
- **THEN** the wrapper SHALL render only the resolved chart content
- **AND** the loading, error, and empty presentations SHALL NOT also render

### Requirement: Chart accessibility wrapper exposes a screen-reader data summary and a color-non-only legend

The Dashboard SHALL provide a shared chart accessibility wrapper that every dashboard chart composes against. Each chart SHALL expose a data summary accessible to assistive technology — a textual representation of the charted data (for example, the series, the time range, and the salient values) consumable by a screen reader without seeing the graphic. Each chart SHALL render a legend that SHALL NOT rely on color alone to distinguish series; the legend SHALL disambiguate series by an additional channel (label, shape, or pattern) so a colorblind or non-visual user can tell series apart.

#### Scenario: Chart exposes a screen-reader data summary

- **WHEN** a dashboard chart renders
- **THEN** the chart SHALL expose a textual data summary accessible to assistive technology
- **AND** the summary SHALL describe the charted series and its salient values without requiring sight of the graphic

#### Scenario: Legend distinguishes series without color alone

- **WHEN** a dashboard chart renders a legend with more than one series
- **THEN** the legend SHALL disambiguate series by a channel other than color (label, shape, or pattern)
- **AND** a user who cannot perceive color SHALL be able to tell the series apart from the legend

### Requirement: Chart numerics use tabular-nums and motion mutates transform, honoring prefers-reduced-motion

Numeric labels rendered on dashboard charts (axis values, data labels, tooltips, legends) SHALL use the `tabular-nums` typographic feature so digits align across rows and updates. Chart motion SHALL animate via `transform` (and/or `opacity`) only; a chart SHALL NOT animate layout properties (such as `width`, `height`, or any property that changes the element's layout box) for bar-height or position changes. Chart motion SHALL honor the user's `prefers-reduced-motion` setting: when reduced motion is requested, charts SHALL disable or minimize animation.

#### Scenario: Numeric labels align with tabular-nums

- **WHEN** a dashboard chart renders numeric labels (axis values, data labels, tooltips, or legend figures)
- **THEN** the labels SHALL use `tabular-nums`
- **AND** digits SHALL align across vertically stacked labels

#### Scenario: Bar-height changes animate via transform, not layout properties

- **WHEN** a dashboard chart animates a change in a bar's height or a series element's position
- **THEN** the animation SHALL mutate `transform` (and/or `opacity`)
- **AND** the animation SHALL NOT animate `width`, `height`, or any property that changes the element's layout box

#### Scenario: Motion is suppressed when the user requests reduced motion

- **WHEN** the user agent has `prefers-reduced-motion` set to reduce
- **THEN** the chart SHALL disable or minimize animation
- **AND** the chart SHALL still render the correct final values
