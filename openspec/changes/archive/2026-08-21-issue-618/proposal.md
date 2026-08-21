## Why

The Mohist App Manager currently depends on a private JSON envelope in model output: Server parses the completed turn, executes the requested management action, synthesizes a follow-up input, and may publish a Server-authored reply. This leaks internal protocol behavior into the Manager conversation and creates a second execution and recovery path; now that ordinary Slack Sessions, the `mo` command surface, reply actions, and liveness projection exist, Manager operations should use those same authoritative boundaries.

## What Changes

- **BREAKING** Remove the `mohistManagerTool` model-output envelope and all compatibility parsing of it. Server no longer interprets assistant text as a management command, executes an operation on the model's behalf, synthesizes a `Task: Follow-up` or tool-result input, or extracts turn output as a Slack reply.
- Run Manager DMs as ordinary Agent Session inputs and turns, using the durable Slack origin, the same reply anchor and collaboration Skill as a normal Slack Connection, and the same recovery behavior after restart or rebinding.
- Issue fresh short-lived, route-scoped Manager capability credentials for each execution. Bind the management and reply credentials to the same immutable Slack origin, authenticated actor, workspace enrollment, Session, dispatch attempt, and expiry; inject each only into its matching execution bridge and reissue both for every turn or recovered execution.
- Expose a small allowlist of Manager operations through a dedicated management capability on the existing `mo` command surface. The `mo slack message send` reply action is a separate, non-management capability with its own anchor validation and Manager outbox route; it is not an exception to, or an eleventh operation in, the management allowlist. Both routes reuse the existing application services and CLI/Web semantics for status, Agent and Connection inspection, routine lifecycle changes, owner operations, diagnostics, and Agent-authored replies.
- Reauthorize every management call against the current actor, enrollment, and target resource. Secret submission, credential reads, permanent deletion, and arbitrary management API access remain unavailable to the Manager capability.
- Make the Manager Agent the sole author of conversational replies through the existing Slack reply action. Acceptance and progress are reactions, and a missing Agent reply is valid silence rather than a synthesized acknowledgement or terminal message.
- Apply the common Slack liveness contract to Manager turns: successful, failed, cancelled, unknown, and recovered outcomes close exactly one terminal reaction, and Manager or Agent Bot messages do not become new Manager inputs.
- Preserve the existing protected CLI/Web authorization and credential-entry paths. No new Slack command grammar, slash command, or secret-bearing conversation flow is introduced.

## Capabilities

- `manager-session-lifecycle`: Manager DMs as ordinary Agent Sessions, including durable Slack-origin continuity, initial and follow-up turn behavior, Slack reply-anchor and collaboration-Skill parity, recovery, and removal of model-output-driven follow-ups and replies.
- `manager-command-capability`: The allowlisted `mo` management operations, their authoritative results and failure behavior, per-call actor and target authorization, reuse of existing application services, and explicit exclusions for secrets, credential reads, permanent deletion, and arbitrary API access.
- `manager-execution-credential`: Per-execution capability credential issuance, binding to actor/origin/enrollment/Session and expiry, execution-environment-only delivery, reissuance after recovery, immediate rejection after authorization changes, and non-persistence or non-disclosure guarantees.
- `manager-slack-reply-liveness`: Agent-authored reply-action delivery, reaction-based acceptance/progress/terminal state, one-terminal-outcome convergence across all Manager outcomes, silence semantics, and managed-Bot loop prevention.

## Impact

- **Server Manager path:** `SlackManagerConversationService`, `SlackManagerToolInvocation`, `SlackManagerToolTurnProcessor`, and `SlackTerminalDeliveryHandler` will stop treating model output as a management protocol and will use the ordinary Agent Session, command capability, reply, and liveness paths.
- **Agent execution contracts:** Manager origin and capability facts must cross the existing AgentJob/Session dispatch boundary without entering Instructions, prompts, transcripts, logs, or durable credential state. Initial launches, follow-ups, and recovery must resolve a fresh credential from the durable Slack origin.
- **Runner and command execution:** the Runner execution environment will expose two typed Manager routes: the exact nine-operation management bridge and the exact `mo slack message send` reply bridge. The latter injects and validates the Server-created Slack anchor and Manager grant before writing a Manager-owned outbox intent; neither route permits unrestricted `mo` commands or arbitrary management endpoints, and credentials remain outside model-visible context.
- **Slack delivery:** Manager outbox handling, reply-action routing, `SlackStatusProjection`, and managed-Bot admission will share the ordinary Connection semantics; Server terminal delivery will no longer author Manager acknowledgements or replies.
- **Built-in Manager assets:** the Manager Instructions/Skill must describe natural-language operation through the command surface and reply action rather than an exact JSON response format.
- **CLI/Web and dependencies:** existing CLI/Web authorization, protected credential submission, and management APIs remain the authority and keep their current behavior. The change adds no Slack-native command grammar or external dependency; capability credentials are ephemeral and are not persisted as durable Manager state.
