# Issue 515 Review

## Findings

### [P1] Thread follow-ups never deliver a final result to Slack

Location: `packages/runner/src/server/followup-handler.ts:211-249`; `packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:9-48`; `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1570-1623`.

The channel follow-up path accepts and dispatches a `SessionInput`, but the runner records only
`session.activity` for the eventual follow-up completion or failure. The only Slack terminal
delivery subscription listens for `AgentJobTerminalDelivery`, which is emitted for the initial
AgentJob and is not emitted for later AgentSession follow-up turns. Consequently, a user gets the
thread acknowledgement but never receives the final result required by
`specs/channel-thread-routing/spec.md`'s acknowledgement/result requirement (lines 60-72).
The implementation needs a durable follow-up completion delivery path that preserves the Slack
connection, channel, and thread provenance.

### [P2] Backpressure rejects messages before the channel acceptance gate

Location: `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1089-1092`; `packages/mohist-slack/src/adapter.ts:103-111`; `packages/mohist-slack/src/transport.ts:59-65`.

`HandleChannelIngressAsync` returns HTTP 409 for every human channel event on a backpressured
Connection before checking whether the message is plain channel text, addressed to another Bot, or
an unbound thread. The adapter only acknowledges Slack after `transport.ingress` resolves, and its
HTTP transport throws on 409, so these messages are not acknowledged and are retried indefinitely.
This violates the channel acceptance gate, which requires non-target messages to be acknowledged
and ignored without persistence; only an attributable accepted work item should be blocked by
backpressure.

### [P2] Rejected follow-ups are acknowledged as if they were continuing

Location: `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:549-575,598-608,1601-1614`.

`RouteFollowupAsync` returns `Status = "rejected"` for missing runtime sessions, recovery/stop
operations, capacity, unknown activity, concurrency limits, and other rejected states. However,
`BuildFollowupAck` has no rejected branch and falls through to `"Continuing."`. The channel path
then persists that misleading acknowledgement and marks the inbox row dispatched even though no
follow-up input was accepted. Owners receive a false success signal instead of the actionable
rejection required for reliable thread interaction.

### [P2] An abandoned launch reservation can permanently block a thread

Location: `packages/server/src/Mohist.Server/Infrastructure/Slack/SlackThreadLaunchReservationStore.cs:22-65`; `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1476-1488`.

Reservations with a null `SessionId` are treated as `InProgress`, but the reservation has no expiry,
release, or recovery state. A process failure after `ReserveAsync` and before the original message
is accepted/bound can leave the row permanently in progress. Once Slack's redelivery window no
longer retries that original message, a later valid mention of the Agent in the same thread always
gets `slack_thread_launch_in_progress` and can never establish the required binding. The durable
reservation needs an ownership timeout or an explicit failure/release path that is itself safe under
concurrency.

### [P2] Ambiguous prompt claim and outbox insertion are still raceable

Location: `packages/server/src/Mohist.Server/Infrastructure/Slack/SlackAmbiguousPromptStore.cs:106-114`; `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1385-1404`; `packages/server/src/Mohist.Server/Infrastructure/Slack/SlackOutboxStore.cs:122-177`.

Two concurrent deliveries of the same ambiguous message through the same winning Connection can both
observe the durable claim before either has inserted its outbox row. Both then pass the outbox's
check-then-insert lookup because the migration has no unique `(ConnectionId, Kind, DispatchRef)`
constraint for required UserAction rows. Depending on SQLite transaction timing this produces two
choose-one prompts or a lock/error response. That violates the at-most-once prompt requirement under
the explicitly required concurrent ingress/redelivery scenario. Claim ownership and delivery
creation need an atomic/idempotent boundary.

## Verification

- The existing suites reported 3,544 Server SpecTests, 1,675 UnitTests, 51 ArchTests, and 8 Slack adapter tests passing, but they do not cover the follow-up final-delivery path or the same-Connection prompt race.

<promise>FAIL</promise>
