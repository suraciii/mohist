## Why

Current runtime completion facts close the entire AgentSession, even though an AgentSession is the stable logical conversation that must remain reusable across multiple Turns. This makes completed, failed, or stopped work appear to end the conversation, blocking later Follow-up and conflating Turn results with TaskRun, AgentJob, and Runtime Session lifecycles.

## What Changes

- Replace Session-terminal transcript facts with a Turn lifecycle: each execution records one `turn.started`, its Turn-scoped facts, and at most one `turn.finished`; a finished Turn returns the AgentSession to idle without removing its Runtime binding.
- Separate AgentSession activity, Runtime binding health, and current/latest Turn in Session queries, command eligibility, API DTOs, and Web projections. A missing Runtime binding is recoverable and is not a closed logical Session.
- Record Follow-up admission separately from Turn completion, distinguishing admitted, rejected, and delivery-unconfirmed operations. An unconfirmed delivery retains its operation identity for reconciliation and is never automatically sent again.
- Apply the same Turn and Follow-up protocol to OpenCode and Pi, for Workflow TaskRuns, AgentJobs, and user-initiated Session commands; Cancel targets only the current Turn and unconfirmed stopping projects activity as `unknown`.
- **BREAKING** Migrate persisted transcripts and pending Runner outbox records to the Turn protocol, then stop accepting, writing, projecting, or dual-writing `session.closed`, `session.followup_completed`, and `session.followup_failed`.

## Capabilities

- `agent-session-turn-lifecycle`: AgentSession supports multiple independently identified Turns; defines Turn start, Turn-scoped transcript ordering, completion/failure/stop outcomes, Session activity and binding projections, current/latest Turn, command eligibility, and consistent Runtime behavior across OpenCode, Pi, Workflow, and Agent launches.
- `agent-session-followup-delivery`: Follow-up operations preserve stable `operationId` and `turnId`, distinguish current-Turn input from a new Turn, expose admitted/rejected/unconfirmed delivery states, reconcile uncertain side effects without duplicate input, and handle Cancel or unconfirmed runtime stopping safely.
- `agent-session-turn-protocol-migration`: Existing transcript history and Runner outbox records transition to the Turn DSL without losing or duplicating effects; after upgrade, legacy Session-terminal event names are neither produced nor consumed.

## Impact

- **Server Session context:** AgentSession domain state, transcript validation/storage, follow-up and cancel commands, Session API DTOs, and Session/AgentOps read projections change from Session-terminal status to activity, binding, and Turn summaries.
- **Runner:** OpenCode and Pi reporters, command handlers, and the durable runtime-event outbox emit and reconcile the Turn and Follow-up protocol for both Workflow and Agent-launch paths.
- **Web:** session event types, live-event handling, transcript rendering, Session lists, command controls, and status displays present current activity and the latest Turn instead of treating historical Turn outcomes as Session terminal states.
- **Persistence and contracts:** persisted transcript and outbox schemas/data require a one-time conversion; Server-Runner and Server-Web event/API contracts remove the legacy terminal Session event names. TaskRun and AgentJob work-result contracts remain independent and unchanged.
