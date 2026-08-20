## Why

Slack Manager currently relies on a private JSON envelope emitted by the model. The Server parses that output, executes a separate Manager tool path, synthesizes follow-up prompts and acknowledgements, and can author the final Slack reply, which makes Manager sessions differ from ordinary Agent sessions and creates protocol, reply-routing, and authorization drift. This change is needed now to make Manager behave as a normal Slack Agent while using the existing `mo` capability surface and current authorization facts.

## What Changes

- **BREAKING** Replace the `mohistManagerTool` model-output envelope with ordinary natural-language Agent turns; remove Server parsing of model output, Manager-specific tool-result follow-ups, and synthesized `Manager request accepted` / terminal reply text.
- Route Manager reads and day-to-day management actions through an explicit allowlist of `mo` CLI capabilities, using the existing application services and authoritative CLI results rather than a second Server-side tool protocol.
- Make Manager turns use the same Slack reply anchor, collaboration Skill, Agent-owned reply action, and message-loop prevention as ordinary Slack Agent Connections. Manager replies are posted by the Manager Agent through the reply action; the Server does not extract turn text to write a reply.
- Issue a short-lived capability credential for each Manager execution. Bind it to the immutable Slack origin, current actor, Enrollment, Session, and expiry; inject it only into the execution environment and never into instructions, prompts, transcripts, logs, Session state, or other durable records.
- Reauthorize every management call against the current actor, Enrollment, and target resource. Reissue credentials for every new turn and recovered execution, and reject expired credentials, changed Enrollment state, removed actors, or unauthorized targets without side effects.
- Keep the Manager capability allowlist limited to status/list/view, Agent creation or mounting, access-policy changes, enable/disable, owner transfer, and diagnostics as applicable. Exclude secret submission, credential reads, permanent deletion, and arbitrary management API access; existing CLI and Web authorization paths remain unchanged.
- Use the ordinary Manager reaction-liveness lifecycle: acknowledge receipt, show progress when applicable, and close exactly one terminal reaction for success, failure, cancellation, unknown outcomes, and recovery, including after duplicate or replayed events.

## Capabilities

- `manager-session-reply`: Manager Slack DMs create or continue a durable Agent Session and execute as a normal Slack Agent, with the authoritative reply anchor and collaboration Skill, Agent-owned natural-language replies, no model-output protocol, no synthesized follow-up text, and no Manager-message loopback.
- `manager-cli-capabilities`: The Manager Agent can use a narrow, explicit `mo` CLI capability allowlist for supported Agent and Slack Connection inspection and daily management actions. CLI results are authoritative, dangerous credential/destructive/arbitrary API operations are unavailable, and existing CLI/Web authorization behavior is preserved.
- `manager-execution-credentials`: Each Manager turn and recovered execution receives a new short-lived credential bound to the immutable Slack origin, actor, Enrollment, Session, and validity window. Credentials are runtime-only, redacted from all model and durable surfaces, and every invocation rechecks current authorization and target ownership.
- `manager-reaction-liveness`: Manager ingress and execution share durable Slack reaction liveness with ordinary Agent Connections, including idempotent receipt/progress handling and exactly one terminal reaction for every known or unknown execution outcome and recovery path.

## Impact

- Server Slack Manager ingress, session launch/follow-up dispatch, built-in Manager instructions, terminal delivery handling, and the Manager-specific JSON tool parser/executor/fence path will change or be removed.
- Server Agent/Session-to-Runner execution contracts and the credential/authorization boundary will change to carry the Manager execution capability without persisting or exposing its value. Durable Slack origin, inbox, Session mapping, and outbox records remain the recovery source of truth.
- The existing Slack outbox, reply-action route, reaction projection, and `mohist-slack` adapter must support Manager delivery through the same ownership, deduplication, loop-prevention, and liveness semantics as ordinary Agent Connections.
- The `mo` CLI capability and its management authorization path become the Manager execution surface; no new dependency is required, but command allowlisting, credential validation, and focused Server, Runner, CLI, and Slack integration tests must be updated. The old Manager model protocol has no compatibility path.
