# Review: Issue 410

## Findings

### P1: AgentJob turns do not append OpenCode events to their AgentSession

`packages/runner/src/runtime/agent-job-executor.ts:95-120` awaits
`OpenCodeRuntime.runTurn` and only calls `attachAgentSession` after the turn
has completed. No AgentJob call path supplies an event callback or posts
`agentSessionRuntimeEvents`. Meanwhile `OpenCodeRuntime.runTurn`
(`packages/runner/src/runtime/opencode/runtime.ts:213-240`) builds its turn
dependencies without an `onEvent` callback, and the runtime's only persistent
global-event subscriber (`runtime.ts:517-536`) watches server connectivity.

As a result, an AgentJob's OpenCode `message.*`, usage, context, and session
lineage events are discarded rather than recorded on the canonical
AgentSession. The final attach/close calls are insufficient: they happen after
the turn and cannot reconstruct transcript or usage events. Wire the AgentJob
execution entry to the existing generic AgentSession runtime-events endpoint
for the duration of the turn, after establishing the physical binding, and add
a fake-runtime test that proves transcript, usage, and lineage are persisted
without allowing those events to terminalize the AgentJob.

### P1: The changed server spec suite is failing

`dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter "FullyQualifiedName~GenericAgentSessionSummarySpecs"`
currently fails `Summary_CarriesFailureCategory_WhenTranscriptHasClosedEvent`
at `packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/GenericAgentSessionSummarySpecs.cs:76`:
it expects `failed` but receives `running`.

The fixture seeds a `session.closed` transcript part but the summary path does
not resolve it as a terminal fact. Fix the seeded binding relationship or the
summary terminal-fact lookup so the test exercises and passes the intended
closed-session behavior. The issue's task acceptance requires the server test
suite to pass.

## Verification

- `npm run test:browser -w packages/web -- coder-session-compact-viewport.spec.ts` passed (11 tests).
- The targeted server spec command above failed (9 passed, 1 failed).

<promise>FAIL</promise>
