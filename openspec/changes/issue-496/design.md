## Context

Issue 484 established `session.activity` as the durable Session fact for activity transitions and terminal execution status. The Session write path and transcript accumulator already persist that type, but obsolete `session.closed` and `session.followup_*` declarations remain in the Server and Web. AgentOps additionally queries `session.activity` transcript parts but synthesizes `session.closed` envelopes for the project feed and routed-failure issue feed.

The Session domain owns accepted runtime facts and activity state. AgentOps is a read-only feed assembler, and the Web consumes those read contracts. Runner no longer produces the retired names. The change is intentionally breaking for callers that still send or consume retired event types; the project has no compatibility requirement. Historical transcript rows must remain untouched.

## Goals / Non-Goals

**Goals:**
- Make `session.activity` the sole terminal Session vocabulary in Server ingestion, transcript mapping, AgentOps feeds, and Web event handling.
- Preserve terminal activity persistence, delivery-idempotency, activity-based command eligibility, feed ordering, and contextual payload data.
- Make project and issue feeds emit the same type as the stored terminal fact and render that type consistently in the Web.

**Non-Goals:**
- Change the Session activity state machine, terminal-status payload shape, delivery-idempotency, or command eligibility rules.
- Alter Runner event emission or add compatibility aliases for retired event names.
- Migrate, delete, or rewrite existing transcript rows.
- Redesign the activity-feed API, event ordering, filtering categories, or navigation-target model.

## Decisions

### 1. Remove retired names at their vocabulary authorities

Remove the three retired constants from `TranscriptEventTypes.cs`, any obsolete transcript mapping branches, and the corresponding Web canonical subscription entries, live-event payload types, view helpers, labels, and special branches. `TranscriptAccumulator.EventTypes` and its mapping remain the Server authority for persistable current runtime events; unknown retired names therefore cannot create transcript parts.

This keeps the accepted language finite and prevents a second, partially supported terminal contract. Tests may name retired values only to prove rejection; production code will not retain them.

Alternative considered: retain deprecated aliases that normalize to `session.activity`. Rejected because the names have no producer, create ambiguous semantics for follow-up outcomes, and contradict the no-new-and-old-vocabulary constraint.

### 2. Project terminal facts are synthesized as `session.activity`

Rename the project-feed terminal loader and envelope factory from the closed terminology to activity terminology. They will continue selecting `TranscriptPartTypes.SessionActivity`, preserving the existing bounded query, terminal-status failure filter, source, subject, timestamp, payload, and final ordering. The synthesized envelope type and its identity label will use `session.activity`; `ProjectEventFilter` will classify that type as an AgentSession event and use an activity-named prefilter helper.

Alternative considered: emit a new reverse-DNS lifecycle event. Rejected because the persisted Session fact is already the public activity event and a second projection type would recreate the mismatch.

### 3. Routed issue failures retain selection semantics while changing only vocabulary

`IssueEventFeedAssembler` will keep selecting only routed, AgentJob-owned failed `session.activity` parts using the established delivery-id and correlation checks. It will construct a `session.activity` CloudEvent and an activity-named stable envelope identity while preserving session, agent, trigger, project, issue, status, and failure fields.

Alternative considered: remove routed terminal failures from the issue feed because they are Session facts. Rejected because the existing AgentOps read contract makes routing failures discoverable from their originating Issue; only the obsolete event name is being removed.

### 4. Web derives terminal presentation from `session.activity` status

Remove retired event types from the canonical transcript union and Agent detail event map. Update the activity-feed event classifier so a `session.activity` payload with a terminal status produces the existing completed or failure presentation, including failure reason/category and targets. Remove the legacy `isSessionClosedEvent` predicate and update transcript/view tests to use current activity events or delete tests whose only subject was retired behavior.

Alternative considered: keep the closed predicate solely for persisted historical rows. Rejected because history is not a second active Web event contract; retaining the branch would leave dead vocabulary in production code. Historical rows are left in storage without migration.

### 5. Cover contracts at their owning boundaries

Server Session tests will assert retired runtime events produce no accepted transcript part and current terminal activity retains idempotent behavior. AgentOps tests will assert project and issue feeds return `session.activity` with the existing context and failure filtering. Web tests will assert canonical subscriptions exclude retired types and activity entries render terminal `session.activity` statuses and targets.

Alternative considered: only search the tree for retired strings. Rejected because a textual absence check cannot prove that the surviving current activity facts remain persisted, surfaced, and rendered correctly.

## Risks / Trade-offs

- [Breaking feed type for stale consumers] -> Document `session.activity` as the sole terminal feed type and update the bundled Web consumer in the same change; no compatibility alias is retained.
- [An alias remains in a low-traffic Web view or test helper] -> Search all production Server and Web sources for the three exact retired names and keep focused tests for canonical subscriptions and terminal presentation.
- [Feed cleanup accidentally changes routing-failure selection or ordering] -> Limit AgentOps changes to type/identity naming and retain existing predicates, sort keys, context projection, and newest-N assembly tests.
- [Historical records use retired type strings] -> Do not run data migration or cleanup; removed code does not mutate stored transcripts.

## Migration Plan

1. Remove retired Server and Web vocabulary declarations and branches, then change project and issue feed envelope construction and filter naming to `session.activity`.
2. Update Server and Web tests for rejection, terminal persistence/idempotency, feed output, and rendering.
3. Deploy Server and Web together so the bundled consumer recognizes the new feed type. No database migration or Runner deployment is required.
4. If rollback is required, redeploy the prior Server and Web versions together. Persisted `session.activity` facts remain compatible because the change does not alter their stored schema or payload.

## Open Questions

None. The target vocabulary, current persisted fact, feed selection rules, and compatibility posture are established by the proposal and specs.
