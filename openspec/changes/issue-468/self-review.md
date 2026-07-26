# Self Review

## Findings

### P1: Tool failure semantics would change across turns

The design says the persisted reducer stores one current failed/not-failed state per tool-call identifier and overwrites it for each observation. That is not the current transcript reduction: `TranscriptEventSummaryProjector` adds an identifier to `failedToolCallIds` whenever any final transcript part for that identifier is failed and never removes it. A tool identifier that is failed in one turn and later completed in another therefore currently contributes one tool error, while the proposed reducer would clear that error. This violates the proposal's requirement to preserve existing event-summary behavior. The design and T-001 need one explicit, tested semantic for later observations of a previously failed identifier, including reuse across turns and Runtime bindings.

### P1: Terminal-fact selection is not defined consistently enough to implement

The spec requires the latest *terminal* activity fact in `(turn, part, identifier)` order. The design instead says every `session.activity` observation replaces the failure pair, while the current projector selects the latest `session.activity` transcript part without a terminal predicate. T-001 neither defines what makes an observation terminal nor persists or derives the specified ordering key. An implementation can therefore either change the current behavior or fail the stated scenario, especially when a later nonterminal activity observation follows a terminal one. The spec, design, and T-001 acceptance criteria must agree on the source events, ordering, and test matrix before implementation.

<promise>FAIL</promise>
