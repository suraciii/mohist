## Why

Today, when a user first `@Bot`s a Mohist agent in a Slack thread that already contains human discussion, the Connection launches the agent on the mention text alone — the prior discussion the user expected the agent to have read is silently absent. The agent produces results on a truncated mental model, and the user only discovers the gap after the fact. The product spec (`docs/agent-connections.md:255-265`) and the Agent API contract (`design/agent-api.md:64-82`) already promise bounded thread history as first-launch background with a completeness guarantee, but neither is implemented: no code reads Slack thread history, the launch chain has no field for startup context, and the first-mention branch (`SlackConnectionRoutes.cs:1177-1183`) throws away all prior content. This issue closes that gap so an agent started inside an ongoing discussion actually starts from that discussion — or refuses to start at all.

## What Changes

- **New Agent API launch channel for bounded startup context.** Callers may supply a bounded external discussion context as first-launch-only background. A new optional field flows through the launch chain (origin → coordinator request → plan → initial-launch command → session input record). It is composed into the agent's execution as **untrusted user input**, never as system instructions, and cannot override Agent Instructions / Runtime / Model / Skills or expand configured capabilities. The launch fingerprint (`AgentLaunchCoordinatorTypes.cs:186-213`) folds it in so replays are detected.
- **Slack Connection imports thread history on first mention.** When a first `@Bot` lands in a thread that already holds prior human messages, the Bot reads the bounded thread history it is permitted to see and imports it as startup background; the mention message itself remains the explicit task. A brand-new thread (mention is the root) imports nothing.
- **Stable oldest-first truncation with explicit marking.** When the bounded range exceeds the context limit, the oldest messages are dropped first; the truncation is shown explicitly in **both** the Slack acceptance reply and the background handed to the Agent — no silent loss.
- **Refuse-on-incomplete.** If the bounded range cannot be read completely (Slack permission, rate-limiting, or failure), the Connection refuses the delegation, creates no `AgentJob`, and tells the user to re-mention later. No stealth launch on partial background.
- **Empty mention still needs a task.** A mention with no task text and no attachments asks the user to add a task and creates no work — consistent with the existing root-mention rule, now applied on the thread-context path.
- **Accepted input stays immutable.** Later Slack edits or deletes of messages already imported do not re-run, undo, or rewrite the accepted input or audit record; users correct via follow-up.

## Capabilities

Each capability below gets a `specs/<name>/spec.md` describing the required behavior for this change. The two compose: the Slack provider consumes the Agent API channel.

- `agent-startup-context`: The reusable Agent API launch channel for bounded external discussion context — the new optional launch field, its first-launch-only semantics, its treatment as untrusted user input that cannot override the agent's execution definition or expand its permissions, and the caller-side completeness contract (refuse rather than submit incomplete background when completeness matters). Specified in agent-API terms, not Slack terms.
- `slack-thread-context`: The Slack Connection behavior for a first `@Bot` in an existing thread — detecting prior human discussion, reading the bounded thread history the Bot can see, oldest-first truncation with explicit marking in both the Slack ack and the agent input, refuse-on-incomplete (no `AgentJob`), the empty-mention task requirement, and immutability of accepted input against later Slack edits or deletes.

## Impact

- **Server (`packages/server`):**
  - `Slack/ISlackApiClient.cs` — add thread-history reading capability (`conversations.replies`); today the interface has no history method at all.
  - `Api/SlackConnectionRoutes.cs` (`HandleChannelIngressAsync` ~`:1147-1183`, `LaunchChannelRootAsync` ~`:1448`) — detect "first mention in existing thread with prior human discussion"; read + bound + truncate history; refuse-on-incomplete; pass startup context to the launcher; thread-context path keeps the empty-mention task requirement.
  - `Agent/Services/AgentLauncher.cs:200-243` (`LaunchConnectionAsync`) — thread the startup-context parameter through.
  - `Workflow/...`/`Agent` launch types — add the optional context field to `AgentLaunchCoordinatorRequest`, `AgentLaunchCoordinatorPlan`, `EnsureInitialLaunchCommand`, `AgentSessionInputRecord`, and the launch fingerprint at `AgentLaunchCoordinatorTypes.cs:186-213`.
  - The exact assignment of "who reads Slack history" (stateless `mohist-slack` adapter forwarding normalized messages in the ingress envelope vs. Server-side fetch) follows the existing adapter/Connection boundary in `design/slack-agent-connection.md`; the split is finalized in `design.md`.
- **`mohist-slack` adapter (`packages/mohist-slack`):** likely grows thread-history fetching (`SlackWebClient`, `adapter.ts`) per the wire-translation boundary; today it only does ingress forwarding and `chat.postMessage`.
- **Tests:** spec coverage for first-mention-with-history import, oldest-first truncation + explicit marking, refuse-on-incomplete (no `AgentJob`), empty-mention task requirement, untrusted-input treatment, and edit/delete immutability — all via fakes (no real Slack, no real time).
- **Docs (`docs/agent-connections.md`):** the 实装差距 note (line 307-309) currently lists "已有 thread 历史" as unimplemented; it updates once delivered. No contract change — the behavior is already the documented target.
