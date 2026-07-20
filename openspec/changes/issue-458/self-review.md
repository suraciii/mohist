# Self Review

## Findings

### 1. High: The runner-only design does not preserve rapid multi-turn transcript boundaries

The terminal-state spec requires reused Workflow AgentSessions to record each turn independently (`specs/workflow-agent-session-terminal-state/spec.md:25`), and the design asserts that a later `session.input` safely resumes the same session without server changes (`design.md:61`, `design.md:65`). `tasks.json` therefore prohibits any server or persistence change while requiring two reused turns to produce `input/activity/close` boundaries (`tasks.json:37`, `tasks.json:40`).

That assumption is not supported by the current persistence path. `TranscriptAccumulator` holds only one pending `_promptText`; `CaptureInput` overwrites it when another `session.input` arrives (`packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs:165`). A `session.closed` event is accumulated as another transcript part but does not flush the pending turn. Persistence occurs on a 200 ms grain timer (`AgentSessionGrain.cs:14`, `AgentSessionGrain.cs:1124`). If the next Workflow task reports its input before that timer flushes, the prior and later turn content can be merged under the later prompt and the first input can be lost. Existing multi-turn tests explicitly call `FlushForTestAsync` between inputs, so they do not establish the behavior assumed by the plan.

The plan must define a deterministic turn-boundary persistence behavior that does not depend on elapsed wall time. This may require a narrow server-side flush/boundary change, while still preserving the issue's no-new-endpoint and no-persistence-model constraints. The specs, design, and tasks must agree on that scope and include coverage for two back-to-back `input/activity/close` sequences without an intervening manual flush or time advance.

### 2. Medium: The verification plan cannot prove the issue's persisted transcript and status acceptance criteria

The design explicitly declines server tests (`design.md:81`), and both tasks verify runner calls through a recording `ServerConnection`. Those tests can prove event production and ordering, but they cannot prove that the Workflow runtime-events route persists a non-empty transcript, keeps turns separate, derives the latest completed/failed status, or exposes those results through Workflow session reads. This is the exact integration boundary where the first finding occurs.

Add a focused server spec using the existing fake/in-process infrastructure that submits Workflow-source input, assistant/tool events, and `session.closed` through the existing route, then verifies transcript content and terminal status. It must include rapid consecutive turns on the same logical and physical session. No browser test or Web implementation change is required.

## Verdict

The event producer approach and best-effort failure isolation are otherwise aligned with issue 458, but the unhandled persistence boundary can still lose Workflow turns while all planned runner tests pass. The plan is not ready to build until the findings above are resolved.

<promise>FAIL</promise>
