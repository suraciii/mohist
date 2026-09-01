# Hermes Webhook

Hermes is the notification delivery adapter for important Issue and Workflow
events. Mohist owns notification policy and message content. Hermes owns
subscription routing and delivery to the selected chat platform.

Product behavior is defined in
[Hermes Notifications](../docs/hermes-notifications.md). This document defines
the component boundary, event contract, HTTP contract, and security rules.

## Design Drivers

- Mohist must remain authoritative for event identity, product state,
  notification policy, message wording, and suggested action.
- Chat delivery must not become a second state machine for Issue, Workflow,
  AgentJob, or Approval state.
- Platform and subscription secrets protect different trust relationships and
  must never be interchangeable.
- Chat delivery is a best-effort last hop. Durable history stays in Mohist.
- A platform reply window can make a valid notification undeliverable. This is
  channel availability, not product failure.

## Model

### Boundary

```text diagram
           +----------------------+
           | Durable Mohist event |
           +-----------+----------+
           +-----------+-----------+
           v                       v
 +-------------------+   +-------------------+
 | Web Inbox history |   | Mohist policy and |
 +-------------------+   |     renderer      |
                         +---------+---------+
                                   |
                                   vone HTTP attempt
                        +---------------------+
                        | Hermes subscription |
                        +----------+----------+
                                   |
                                   v
                           +---------------+
                           | Chat platform |
                           +---------------+
```

- Mohist owns event selection, enabled notification types, Issue context, the
  rendered `body`, the suggested action, and Web Inbox history.
- Hermes owns subscription authentication, destination selection, and platform
  delivery.
- The chat platform owns provider acceptance, rate limits, and reply-window
  rules.
- Hermes must not reinterpret `body` through an Agent or LLM. It delivers the
  rendered message directly.
- Hermes must not own Issue or Workflow arbitration, message semantics, or a
  second copy of Mohist state.
- The chat platform must not own Mohist retry, completion, or Approval
  decisions.

### Secrets

The **platform-level secret** belongs to the Hermes webhook platform. Mohist
neither stores nor sends it.

The **subscription-level secret** authenticates Mohist requests to one Hermes
subscription. Mohist `Secret` holds this value.

These secrets are not interchangeable. Using the platform secret for one
subscription would grant platform-wide trust.

## Semantics

### Delivery

Mohist sends one best-effort HTTP request. The Web Inbox remains the durable
record. Hermes has no Mohist outbox, retry queue, or dead-letter queue for this
last hop.

A network error or non-success response is a delivery failure. It does not
change or block Issue, Workflow, AgentJob, or Approval state. The webhook does
not promise replay or exactly-once delivery.

Some platforms accept Bot messages only for a limited period after the user's
latest message. Hermes reports an expired Weixin reply window as a delivery
failure, even when the platform labels it rate limited. Telegram is the
recommended default for completion notifications. Use Weixin while its
conversation remains active.

### Configuration

The configuration has three settings:

- `WebhookUrl`: the Hermes subscription endpoint. Missing or empty disables
  delivery.
- `Secret`: the optional subscription-level signing secret.
- `EnabledTypes`: the allowlist of `notificationType` values. Events outside
  the list produce no request.

Hermes must deliver the rendered `body` and select an explicit destination when
the platform has no default home channel. Setup commands and operator checks
belong in [Hermes Notifications](../docs/hermes-notifications.md).

### Event Types

`notificationType` is the compact subscription and template value.
`eventType` preserves the triggering Mohist event for traceability.

The supported pairs are:

- `approval_requested` for `com.mohist.workflow.stage.approval-requested`
- `workflow_failed` for `com.mohist.workflow.run.failed`
- `issue_completed` for `com.mohist.issue.completed`
- `issue_started` for `com.mohist.issue.work-started`
- `agent_response_failed` for `com.mohist.agent.job.failed`

All types are enabled by default except `issue_started`.

### HTTP Request

Mohist sends one HTTP `POST` to `WebhookUrl` with:

- `Content-Type: application/json`
- `X-Mohist-Event: <notificationType>`
- `X-Hub-Signature-256: sha256=<lowercase-hex-hmac>` when `Secret` is set
- the camelCase JSON payload below as the request body

A 2xx response confirms only that Hermes accepted this delivery request. A
non-2xx response or transport failure is a last-hop delivery failure. Mohist
does not use the response body as product state.

The payload is a notification projection, not a CloudEvent envelope. It carries
source identity and occurrence time so operators can correlate a delivery with
Mohist history without receiving the complete event or internal state.

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

The payload fields are:

- `notificationType`: one supported value, repeated in `X-Mohist-Event`.
- `eventType`: the full type of the source event.
- `sourceEventId`: the stable source-event identity, not a webhook-attempt ID.
- `occurredAt`: the source-event time in ISO 8601 form.
- `projectId`, `issueNumber`, `issueTitle`: the required Issue identity and
  current title used to render the notification.
- `epicNumber`: present only when the Issue currently belongs to an Epic.
- `workflowRunId`: present when the source event identifies a WorkflowRun.
- `stage`: present for an Approval Point notification.
- `failureReason`: present for Workflow or Agent response failure when a short
  reason is available. It never contains a stack trace.
- `suggestedAction`: actionable text that includes the Issue number.
- `body`: the complete user-facing message rendered by Mohist.

Nullable fields are omitted rather than sent as JSON `null`.

### Signature

When `Secret` is configured, Mohist computes HMAC-SHA256 over the exact UTF-8
bytes sent as the JSON request body. It encodes the digest as lowercase
hexadecimal and sends:

```text literal
X-Hub-Signature-256: sha256=<lowercase-hex-hmac>
```

Hermes verifies this signature with the subscription-level secret before
template rendering or chat delivery. The platform-level secret never
participates in the calculation. Verification uses the received bytes rather
than reserialized JSON because whitespace, field order, or any body byte
changes the signature.

When `Secret` is absent, Mohist sends no signature header. This is an explicit
deployment trust choice, not sender authentication. HMAC authenticates body
integrity and possession of the subscription secret. It does not provide
freshness, replay protection, durable delivery, or exactly-once semantics.
