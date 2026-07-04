## Why

When a user launches a generic agent session from the Web Agent workbench, the
runner really executes — consuming tokens, calling tools, occupying runner
capacity — but the session detail page stays at "Waiting for activity...",
"0 turns", and never leaves `running`. The transcript API returns turns with
`messages: 0`, `events: 0` while usage shows consumed tokens, so the agent
session feature (#132, the last unclosed loop of the workbench) is effectively
useless to the user: launch dispatches, the runner runs, the read endpoints
exist, yet nothing the agent did is visible.

Code review (2026-07-03) confirmed the generic-session event pipeline is wired
end-to-end and symmetric, so this is a real break, not a missing pipe. Two
leading root causes are pinned in code but **not yet confirmed by reproduction**;
the change must reproduce → localize → fix → lock down with regression coverage
on the generic (`sessionId`) axis, deliberately kept separate from the
issue/workflow session axis (#47, a non-goal here).

## What Changes

- Generic agent sessions record a **non-empty, queryable transcript**: assistant
  text replies, tool calls, and usage appear in the session detail page and
  `GET .../agent-sessions/{sessionId}/transcript`, for both the initial turn and
  follow-up turns. (Root cause pending reproduction; leading candidate is the
  session identity being lost between dispatch and the runner's event emitter,
  causing every `emitSessionEvent` to be silently dropped.)
- A generic session reaches a **terminal state** (`completed`/`failed`) when the
  agent job finishes. **Fix the success path**: today `acp-agent.ts` suppresses
  `session.closed` for a succeeding generic agent-job (the ACP session is cached
  for follow-ups), so the server's terminal-state derivation — which keys off a
  persisted `session.closed` transcript part — never fires and the session hangs
  in `running` forever. The job-completion signal must be decoupled from the
  runner's cached ACP-session lifetime.
- Harden the launch → dispatch contract: a generic launch always mints and
  propagates a **non-null `AgentSessionId`** that the runner uses verbatim as the
  runtime-events target, and an unresolved session target is **observable**
  (logged), not silently swallowed — so this class of failure can't go
  undiagnosed again.
- Add regression coverage on the generic (`sessionId`) axis, kept distinct from
  the issue/workflow session axis (#47).

Non-goals: changing issue/workflow session transcript behavior (#47); adding
interactive/resume sessions (#133); rewriting the transcript persistence model
(#100, the `TranscriptAccumulator` deferred flush).

## Capabilities

- `agent-session-launch`: The generic launch → `AgentJob` dispatch contract —
  the dispatch envelope carries a non-null `AgentSessionId` that the runner uses
  verbatim to route runtime events; a generic launch always mints and propagates
  it; an unresolved session target is observable rather than silently dropped.
  (Addresses the null-dispatch hypothesis and the latent silent-drop footgun at
  `session-events.ts:65`.)
- `generic-agent-session-transcript`: A generic (agent-launch) session records
  and surfaces a non-empty transcript — assistant text, tool calls, usage — for
  the initial turn and follow-up turns, queryable via the session detail page and
  the transcript API, on the `sessionId` axis distinct from issue/workflow
  sessions.
- `generic-agent-session-terminal-state`: A generic session reaches
  `completed`/`failed` when the agent job finishes — including the success path —
  with the job-completion signal decoupled from the runner's cached ACP-session
  lifetime, so the runner may keep its ACP session for follow-ups without the
  server-side session hanging in `running`.

## Impact

- **Runner (TypeScript)**: `actions/acp-agent.ts` (success-path close emission);
  `actions/acp/session-events.ts` (`sessionTargetFromContext` / `emitSessionEvent`
  — observable drop instead of silent return); `actions/acp/session-strategies.ts`
  (generic vs ephemeral routing); `runtime/executor.ts` and
  `server/connection.ts` (`agentSessionId` context wiring from the dispatch
  envelope).
- **Server (C#)**: `Agent/Grains/AgentJobGrain.cs` (`BuildDispatch`
  `AgentSessionId` propagation, `CloseGenericSessionOnFailureAsync`);
  `Api/AgentSessionLaunchRoutes.cs` (launch-time minting);
  `Sessions/Grains/AgentSessionGrain.cs` and `Sessions/Services/AgentSessionQuerier.cs`
  (terminal-state derivation from `session.closed`); the
  `agent-sessions/{projectId}/{sessionId}/runtime-events` endpoint in
  `Api/RunnerRoutes.cs`.
- **Web (React)**: session detail page (`agent-sessions/:id`) transcript/turn
  rendering — likely no logic change once data lands, but must be verified
  against a real execution end-to-end.
- **APIs/Data**: no new endpoints or schema changes anticipated; behavior fix on
  the existing transcript / terminal-state contracts.
- **Tests**: runner unit + spec on the generic axis (transcript emission,
  success-path close, no-silent-drop); server unit + spec (terminal-state
  derivation on success, `AgentSessionId` propagation); regression guards that
  isolate the generic (`sessionId`) axis from the issue/workflow axis (#47).
