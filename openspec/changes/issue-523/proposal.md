## Why

Mohist already records AgentSession activity, transcript evidence, inputs, turns, and runtime state, but the Web must make those facts intelligible and actionable when a user needs to supervise an Agent. A coherent fallback surface is needed now so users can follow a session's progress, understand whether it needs intervention, and safely continue or recover it without treating the Web as a separate Agent runtime.

## What Changes

- Make the Web AgentSession page explain the session's origin and current state before its raw transcript, including its associated Agent or Workflow work, current activity, inputs and turns, latest result, runtime context, usage, and actionable failure evidence.
- Present the ordered transcript and tool activity together with the acceptance and delivery state of each SessionInput and the AgentTurn that processed it, including clear queued, executing, terminal, and unknown states.
- Provide Web controls to submit a follow-up, cancel a queued turn or request a stop for an active turn, and perform Compact or Reset only when the Session state makes each operation safe.
- Preserve the authoritative Session result and activity state after commands and live updates; unavailable or uncertain operations must remain visibly unavailable rather than being retried or represented as completed by the client.

## Capabilities
- `agent-session-web-tracking`: The Web read experience for an AgentSession: source and work context, activity and turn/input status, ordered transcript and tool evidence, runtime/context usage, terminal results, and explicit unknown or failure states.
- `agent-session-web-operations`: The Web command experience for an AgentSession: follow-up delivery, queued-turn cancellation, active-turn stop requests, Compact, and Reset, including availability rules, confirmation, accepted/queued/terminal/unknown outcomes, and view convergence after a command.

## Impact

- **Web (`packages/web`)**: session detail data sources and page shell; shared transcript, follow-up, turn-control, and recovery widgets; session-related query invalidation and live-event handling.
- **Server (`packages/server`)**: AgentSession read models and query routes must expose the tracking facts; follow-up, turn-control, compact, and reset APIs must return authoritative command outcomes for the Web to present.
- **Product/design docs**: `docs/web-ui.md`, `docs/agents.md`, and the Session/API design contracts are aligned with the delivered Web behavior.
- **Dependencies**: no new external dependency is expected.
