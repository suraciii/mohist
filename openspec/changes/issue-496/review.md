## Findings

### P1: Terminal activity failures are displayed as successful completion

`packages/web/src/entities/agent/model/types.ts:75` narrows `session.activity` to only `activity` and `observedAt`, dropping the terminal `status`, `failureReason`, and `failureCategory` carried by the Server. `packages/web/src/entities/session/model/view/compact.ts:70` then maps every idle activity to `completed`, while `packages/web/src/entities/session/model/view/chat.ts:256` only closes the turn and never adds the failure part. A terminal activity with `status: "failed"`, `"timeout"`, or `"cancelled"` is therefore rendered as completed or with no failure detail. Preserve and interpret the terminal context on `session.activity`, and cover all terminal statuses as required by the Web acceptance criteria.

### P1: Routed AgentJob terminal failures cannot reach the Issue feed

`packages/server/src/Mohist.Server/AgentOps/Services/IssueEventFeedAssembler.cs:94` requires a `session.activity` part whose correlation key/id and payload `deliveryId` are the canonical `agent-job:{id}:terminal` value. Actual terminal delivery takes `AppendTerminalCloseAsync` in `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:817`, which persists that value only as payload `operationId`; `packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs:275` assigns `session.activity` as the correlation key for this part. Thus real routed failed deliveries fail the query and projection predicates, despite the unit test constructing an unattainable row. Align the persisted terminal identity and assembler selection, and add an end-to-end/spec-level test from an AgentJob terminal failure to the Issue feed.

### P1: The workflow timeout change violates the CI time budget and is outside this change

`packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-local.workflow.yaml:161` changes the verify timeout from five to ten minutes. This issue only retires Session event vocabulary, and `design/testing.md:98` requires every CI job to have a five-minute timeout, treating a timeout as an abnormal condition to diagnose. Restore the five-minute limit; any cold-workspace verification-cost work needs its own scoped change that meets the repository budget.

<promise>FAIL</promise>
