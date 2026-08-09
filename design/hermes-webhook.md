# Hermes Webhook

Mohist uses Hermes as a notification delivery adapter for important Issue and
Workflow events. This boundary keeps product decisions in Mohist while allowing
Hermes to specialize in chat platforms such as Telegram and Weixin.

See [Hermes Notifications](../docs/hermes-notifications.md) for notification
semantics, enablement, setup, and operator verification. This document owns only
the architectural boundary and the HTTP wire and security contracts.

## Drivers

### Keep Product Authority in Mohist

Notification delivery must not become a second state machine for an Issue or
Workflow. Mohist Server remains authoritative for event identity, current
product state, notification policy, message wording, and suggested action.
Hermes receives an already rendered message and owns only subscription routing
and delivery to the selected chat platform.

This split prevents chat availability, template behavior, or a Hermes Agent
loop from changing whether work succeeded or which action is valid. The Hermes
subscription therefore delivers `body` directly; it must not reinterpret the
message through an LLM.

### Use Separate Trust Boundaries

Hermes has two independent secret levels because they protect different
relationships:

- The **platform-level secret** belongs entirely to the Hermes webhook platform.
  Mohist neither stores nor sends it.
- The **subscription-level secret** authenticates Mohist requests to the Mohist
  Hermes subscription. Mohist `Secret` must match this value.

Treating these secrets as interchangeable would expand the trust granted to one
subscription into platform-wide trust. Configuration and diagnostics must name
the level explicitly.

### Keep Durable History Outside the Last Hop

A chat message is useful for immediate attention but is not a durable product
record. The Web Inbox retains what happened; the Hermes HTTP request is one
best-effort delivery attempt. Delivery failure never changes or blocks Issue,
Workflow, AgentJob, or Approval state.

```text diagram
                            +--> [Web Inbox: durable history]
                            |
[Durable Mohist event] -----+
                            |
                            +--> [Mohist policy and renderer]
                                      |
                                      | signed JSON, one best-effort attempt
                                      v
                                [Hermes subscription]
                                      |
                                      v
                                [Chat platform]

Mohist owns: event, state, policy, rendered body
Hermes owns: subscription, destination, platform delivery
```

The event bus provides at-least-once delivery to Mohist subscription handlers;
see [Event Bus](eventbus.md). Once the Hermes handler accepts the event for
background delivery, the HTTP last hop has no outbox, retry queue, or DLQ. A
network error or non-success response is logged and consumed. This avoids making
external chat availability part of product execution, but it also means the
webhook does not promise replay or exactly-once delivery.

### Treat Reply Windows as Channel Availability

Some chat platforms restrict when a Bot may initiate a message. Through iLink,
Weixin permits delivery only for a limited window after the user's latest
message, approximately 48 hours in current practice. After expiry it can return
`ret=-2`, which Hermes reports as rate limited even though no rate quota was
exhausted.

This is a channel-availability constraint, not a Mohist state failure. It is
especially unsuitable for completion notifications after long-running work.
Telegram is therefore the default recommendation; use Weixin while its
conversation is active. Sending a message to the Bot opens a new platform
window, but does not replay notifications missed during the old one.

## Boundary Contract

| Owner | Owns | Must not own |
|---|---|---|
| Mohist Server | Event selection, enabled notification types, Issue context, rendered `body`, suggested action, and Web Inbox history | Chat credentials, chat delivery state, or platform retry policy |
| Hermes | Subscription authentication, destination selection, and chat-platform delivery | Issue or Workflow arbitration, message semantics, or a second copy of Mohist state |
| Chat platform | Provider acceptance, rate limits, and reply-window rules | Mohist retry, completion, or Approval decisions |

The minimum Mohist configuration contract is:

| Setting | Contract |
|---|---|
| `WebhookUrl` | Target Hermes subscription endpoint. Missing or empty disables Hermes delivery. |
| `Secret` | Optional subscription-level secret used to sign the exact request body. It is never the Hermes platform-level secret. |
| `EnabledTypes` | Allowlist of `notificationType` values. Events outside the list produce no request. |

The Hermes subscription must accept the request contract below, deliver the
rendered `body` without an Agent loop, and select an explicit destination when a
platform has no default home channel. Exact Mohist and Hermes commands, file
locations, service reloads, and end-to-end checks belong in
[Hermes Notifications](../docs/hermes-notifications.md), not in this design.

## Event Types

`notificationType` is the compact subscription and template value.
`eventType` preserves the triggering Mohist event for traceability.

| `notificationType` | `eventType` | Enabled by default |
|---|---|---:|
| `approval_requested` | `com.mohist.workflow.stage.approval-requested` | Yes |
| `workflow_failed` | `com.mohist.workflow.run.failed` | Yes |
| `issue_completed` | `com.mohist.issue.completed` | Yes |
| `issue_started` | `com.mohist.issue.work-started` | No |
| `agent_response_failed` | `com.mohist.agent.job.failed` | Yes |

## HTTP Request

Mohist sends one HTTP `POST` to `WebhookUrl` with:

- `Content-Type: application/json`
- `X-Mohist-Event: <notificationType>`
- `X-Hub-Signature-256: sha256=<lowercase-hex-hmac>` when `Secret` is set
- The camelCase JSON payload below as the request body

Any 2xx response confirms only that Hermes accepted this delivery request. A
non-2xx response or transport failure is a last-hop delivery failure and follows
the best-effort policy above. Mohist does not use the response body as product
state.

The payload is a notification projection, not a CloudEvent envelope. It carries
the source event identity and occurrence time so operators can correlate a chat
delivery with durable Mohist history without exposing the complete event or
internal state.

```json
{
  "notificationType": "approval_requested",
  "eventType": "com.mohist.workflow.stage.approval-requested",
  "sourceEventId": "evt_123",
  "occurredAt": "2026-07-03T12:01:00+00:00",
  "projectId": "proj_123",
  "issueNumber": 42,
  "epicNumber": 7,
  "issueTitle": "Add login rate limiting",
  "workflowRunId": "wr_123",
  "stage": "plan",
  "suggestedAction": "approve 42",
  "body": "Issue #42 is waiting for an approval decision in plan. Next: approve 42"
}
```

| Field | Contract |
|---|---|
| `notificationType` | One value from the event table and the value repeated in `X-Mohist-Event`. |
| `eventType` | Full Mohist type of the source event. |
| `sourceEventId` | Stable source-event identity for correlation; it is not a new webhook-attempt ID. |
| `occurredAt` | Source-event occurrence time in ISO 8601 form. |
| `projectId`, `issueNumber`, `issueTitle` | Required Issue identity and current title used to render the notification. |
| `epicNumber` | Present only when the Issue currently belongs to an Epic. |
| `workflowRunId` | Present when the source event identifies a WorkflowRun. |
| `stage` | Present for an Approval-point notification. |
| `failureReason` | Present for Workflow or Agent response failure when a short reason is available; never contains a stack trace. |
| `suggestedAction` | Actionable text that always includes the Issue number. |
| `body` | Complete user-facing message rendered by Mohist in its configured language. This is the default and recommended Hermes template input. |

Nullable fields are omitted rather than sent as JSON `null`.

## Signature Contract

When `Secret` is configured, Mohist computes HMAC-SHA256 over the exact UTF-8
bytes sent as the JSON request body. It encodes the digest as lowercase
hexadecimal and sends the GitHub-compatible header:

```text literal
X-Hub-Signature-256: sha256=<lowercase-hex-hmac>
```

Hermes verifies this signature with the Mohist subscription's secret before
template rendering or chat delivery. The platform-level Hermes secret never
participates in this calculation. Changing JSON whitespace, field order, or any
body byte changes the signature, so verification must use the received bytes
rather than reserialized JSON.

When `Secret` is absent, Mohist sends no signature header. That is an explicit
deployment trust choice, not evidence that the sender is authenticated. HMAC
authenticates body integrity and possession of the shared subscription secret;
it does not itself provide freshness, replay protection, durable delivery, or
exactly-once semantics.
