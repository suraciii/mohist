---
status: stable
---

# Outbound Webhook

An outbound webhook is an event subscription within a Project. It selects
which events to send, where to send them, and how to authenticate according to
the receiver's requirements. For each CloudEvent, every matching subscription
renders structured JSON according to CloudEvents and POSTs it to its TargetUrl.

The v1 product position is: **Mohist is a general HTTP webhook producer, not an
adapter for a particular receiver.** A subscription configures the
authentication required by its receiver, such as `Authorization: Bearer`,
Basic, or a custom header such as `X-Webhook-Secret`. Mohist sends a standard
CloudEvents 1.0 request. **A Mohist-specific HMAC signature is outside v1.** It
is retained only for old-subscription compatibility; a new subscription is
unsigned by default.

Outbound webhook is Mohist's external Open Host Service and Published Language.
Mohist publishes CloudEvents through a standard webhook, and any downstream
consumer subscribes according to the CloudEvents standard. Mohist does not
specialize for Buzz, CI, or another receiver. This is not an Agent response,
which belongs to [`event-routing.md`](event-routing.md); a chat notification,
which belongs to [`hermes-webhook.md`](hermes-webhook.md); or a Workflow
`call_webhook` Action, which actively calls from one Workflow step.

Outbound webhook provides one-way Mohist -> downstream delivery only. An
authenticated inbound flow from a downstream system to Mohist is a separate
future capability outside this spec. This document therefore does not define
loop protection for returning events or security restrictions on an outbound
target.

## Model

WebhookSubscription is a Project-scoped resource. It declares which events to
send, where to send them, and which authentication to use. It owns no Session,
execution, or receiver state.

The resource has a stable Project-scoped identity and a readable name. Its
target is an `http` or `https` URL. Event selection is either all event types or
a non-empty selected set, with an optional CEL filter. Authentication is
`none`, `bearer`, `basic`, or `custom`. Lifecycle is active, disabled, or
archived.

Credentials are stored separately from the subscription. Plaintext lives only
in process memory and never enters the subscription, logs, transcript, API,
CLI output, or failure record.

The following invariants always hold:

- With selected event types, the set is nonempty and every type exists in the
  event catalog. Writing rejects an unknown type.
- Writing rejects an invalid CEL filter. An empty filter is valid and means no
  additional filter.
- `TargetUrl` is a valid `http` or `https` URL; writing rejects another value.
- `AuthType` is one of `none`, `bearer`, `basic`, or `custom`.
- Credentials are encrypted at rest and redacted from every read surface.

Web and CLI configure the same WebhookSubscription and do not create separate
local configurations.

## Semantics

### Subscription Matching

When an event occurs, load subscriptions with `Status = Active` in its Project
and evaluate each one:

1. **Event selection**: With `EventSelectionMode = selected`, the event `type`
   must be in `EventTypes`. `all` passes every type.
2. **Optional advanced CEL**: When `Match` is nonempty, evaluate it with the
   same semantics as the routing table. Delivery requires both checks to pass.

- Every matching subscription delivers independently as fanout. There is no
  first match or ordering. Those semantics belong to routing-table Approval,
  where only one decision maker can act. The subscription table therefore has
  no `Position`.
- Subscriptions load at event time, so configuration applies immediately and is
  not fixed at startup.

### Payload Rendering

The body is the CloudEvent serialized as JSON in CloudEvents structured content
mode. It needs no template engine because CloudEvent is the Published Language.
Extensions become top-level custom properties according to the standard:

```json
{
  "specversion": "1.0",
  "id": "evt_123",
  "source": "mohist://proj_123",
  "type": "com.mohist.issue.completed",
  "time": "2026-08-01T12:01:00+00:00",
  "datacontenttype": "application/json",
  "data": { "issueNumber": 42, "title": "Add login rate limiting" },
  "projectid": "proj_123",
  "issue": "42",
  "epic": "7",
  "workflowrunid": "wr_123"
}
```

`data` is the event payload. Top-level `projectid`, `issue`, `epic`,
`workflowrunid`, and similar properties come from business-lineage extensions.
They are the only authority for downstream business location and receive no
second transformation.

### Authentication and Sending

```text literal
body   = rendered CloudEvent JSON as UTF-8 bytes
POST TargetUrl
  Content-Type: application/cloudevents+json
  Authorization: Bearer <token>           # AuthType=bearer
  Authorization: Basic <base64(user:pass)> # AuthType=basic
  <Custom-Header>: <value>                 # Each configured AuthType=custom header
  X-Hub-Signature-256: sha256=<hex>        # Compatibility only, with an old signature secret
```

- `Content-Type` is the CloudEvents 1.0 structured JSON media type
  `application/cloudevents+json`.
- Subscription `AuthType` selects headers. `none` adds none; `bearer` and
  `basic` add `Authorization`; `custom` adds each user-configured header. Values
  are read from `ISecretStore` and assembled only while sending, without logs.
  A custom header cannot override transport-controlled or CloudEvents-reserved
  headers: `Host`, `Content-Length`, `Transfer-Encoding`, `Content-Type`,
  `X-Hub-Signature-256`, or `X-Mohist-*`.
- **Signature is compatibility, not the v1 path.** Only an old subscription with
  an HMAC signature secret adds GitHub-style HMAC-SHA256 in
  `X-Hub-Signature-256`. A new subscription defaults to `AuthType=none` and no
  signature. V1 does not expose a signature secret on create.
- The fixed timeout is 15 seconds. `OperationCanceledException` records a
  `request timed out` failure.

### Failure Behavior

The outbound HTTP POST is best effort. A non-2xx response, connection refusal,
DNS or TLS error, or timeout writes a log and a durable failure record without
retry. It neither blocks the event stream nor changes Issue or Workflow
execution. The record includes subscription, target, event ID, HTTP response
status when available, duration, and error summary for Web and CLI inspection.
Delivery uncertainty is visible rather than silent. A 2xx response succeeds
and writes no record.

Automatic retry, successful-delivery history, manual redelivery, and test
delivery are outside v1.

## Examples

Create a subscription with event selection and a receiver-defined custom
header, with no CEL or bridge:

```bash
# List selectable event types by group
mo webhook event-types

# Subscribe to Issue completion with the receiver's custom header
mo webhook subscription create my-ci-hook \
  --event com.mohist.issue.completed \
  --target-url 'https://ci.internal/mohist' \
  --auth-type custom \
  --auth-header 'X-Webhook-Secret=<receiver-secret>'

# Advanced: narrow the selected events with CEL
mo webhook subscription create my-fine-hook \
  --event com.mohist.issue.completed \
  --match 'event.issue == "42"' \
  --target-url 'https://ci.internal/mohist'
```

After a matching `issue.completed` event, the receiver gets:

```http
POST /mohist HTTP/1.1
Host: ci.internal
Content-Type: application/cloudevents+json
X-Webhook-Secret: <receiver-secret>

{"specversion":"1.0","id":"evt_123","type":"com.mohist.issue.completed","issue":"42",...}
```

The receiver parses according to CloudEvents: `type` and `source` identify the
event, `issue`, `epic`, and `workflowrunid` identify its business location, and
`data` contains its payload. Mohist does not know how the receiver uses it.

## Status

The v1 contract is implemented: general HTTP, configurable endpoint
authentication with none, bearer, basic, and custom; event selection with CEL
as an advanced filter; the CloudEvents media type; 2xx success semantics; and
failure records containing HTTP status and duration. Existing subscriptions can
still use their HMAC signature secret, so behavior does not change silently.

V1 does not include test delivery, successful-delivery history, manual
redelivery, a Web management UI, automatic retry, or a Mohist-specific signature
protocol with key rotation.
