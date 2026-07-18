### Requirement: Lazy-render granularity is the turn or tool row, not the whole transcript container

The transcript SHALL apply its visibility / lazy-render strategy at the granularity of individual turns or individual tool rows, rather than at the granularity of the whole `role="log"` container. The single container-level `content-visibility: auto` applied to the entire transcript body SHALL be removed in favor of per-turn or per-row application. The chosen granularity SHALL apply uniformly across all turns and all tool rows of the same kind.

#### Scenario: Container-level content visibility is removed
- **WHEN** the transcript body renders
- **THEN** the `role="log"` container element SHALL NOT carry a container-level `content-visibility` style
- **AND** visibility/lazy-render decisions SHALL be applied to individual turn or tool-row elements instead

#### Scenario: Lazy-render strategy applies uniformly across rows
- **WHEN** multiple turns or tool rows render in the transcript
- **THEN** each turn or tool row of the same kind SHALL be subject to the same visibility strategy
- **AND** no individual row SHALL be special-cased

### Requirement: Off-screen content is deferred without changing the on-screen rendering contract

A turn or tool row that is off-screen SHALL be a candidate for deferred or skipped rendering, while a row that is on-screen SHALL render its full content indistinguishably from the non-lazy baseline. The deferred rendering SHALL NOT alter any user-visible aspect of an on-screen row, including its status symbol, verb-led title, key parameters, duration, file-change inline statistics, whole-row failure styling, expand control, or expanded typed detail content. Stable anchors (`data-turn-id`, `data-turn-index`, `data-tool-call-id`, `data-tool-state`, `data-tone`) SHALL remain present on every row regardless of whether the row is currently on-screen.

#### Scenario: On-screen rows render identically to the non-lazy baseline
- **WHEN** a turn or tool row is in the visible viewport
- **THEN** the row SHALL render its full content (status symbol, title, parameters, duration, inline edit stats where applicable, failure styling where applicable, expand control)
- **AND** SHALL be indistinguishable from a row rendered without the lazy strategy

#### Scenario: Stable anchors remain present on off-screen rows
- **WHEN** a turn or tool row is currently off-screen
- **THEN** the row element SHALL still expose its stable anchors (`data-turn-id` / `data-turn-index` for turns, `data-tool-call-id` / `data-tool-state` / `data-tone` for tool rows)
- **AND** programmatic lookups against those anchors (for example `querySelector('[data-tool-state="failed"]')`) SHALL continue to resolve

### Requirement: Row expansion remains correct under the lazy-render strategy

Expanding a previously off-screen tool row SHALL produce the same typed detail content (terminal/command output, inline diff, or result summary) as expanding an on-screen row of the same kind. The lazy-render strategy SHALL NOT cache, drop, or alter a row's expanded detail content based on whether the row was on-screen at the time of expansion. The row expansion contract (default-collapsed single row, click to reveal typed detail) established for the flat timeline SHALL be preserved.

#### Scenario: Expanding a row that was off-screen renders full detail
- **WHEN** a tool row that was off-screen is scrolled into view and then expanded
- **THEN** the row SHALL reveal the same typed detail content that would have been rendered without the lazy strategy

#### Scenario: Expansion does not depend on prior on-screen state
- **WHEN** two rows of the same kind are expanded, one of which was on-screen at mount and one of which was off-screen
- **THEN** both rows SHALL reveal identical typed detail content

### Requirement: Long-transcript smoothness is verified structurally, not by wall-clock timing

Because the project forbids wall-clock-based test assertions, the lazy-render strategy's effect on long-transcript smoothness SHALL be verified through structural properties (for example: the set of rows actually mounted or rendered at a given scroll position, the count of rows undergoing deferred work, or the application of the lazy-render attribute to off-screen rows). Tests SHALL NOT assert smoothness via `elapsed < N` thresholds or real-time measurements.

#### Scenario: Lazy-render behavior is asserted structurally
- **WHEN** a long transcript (many turns and tool rows) is exercised in a test
- **THEN** the test SHALL assert the lazy-render outcome via structural properties (mounted row set, lazy attribute application, or deferred-row count)
- **AND** SHALL NOT assert via wall-clock elapsed time
