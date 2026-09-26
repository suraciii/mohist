# Outbound Webhook

An outbound webhook sends selected Project events to an external HTTP target.
Each matching subscription produces one CloudEvents 1.0 structured JSON request.
This document defines the resource, selection, authentication, payload, and
failure boundary.

## Matching and Delivery

For an event in a Project, Mohist loads that Project's active subscriptions and
evaluates each independently:

```text diagram
          +---------------+
          | Project event |
          +-------+-------+
                  |
                  v
      +----------------------+
      | active subscriptions |
      +-----------+----------+
                  |
                  v
      +-----------------------+
      | event and CEL filters |
      |         pass?         |
      +-----------+-----------+
        +---------+---------+
        vno                 vyes
 +-------------+   +-----------------+
 | no delivery |   | CloudEvent JSON |
 +-------------+   +--------+--------+
                            |
                            v
               +-------------------------+
               | receiver authentication |
               +------------+------------+
                            |
                            v
                   +----------------+
                   | POST TargetUrl |
                   +--------+-------+
                            |
                            v
                        +------+
                        | 2xx? |
                        +---+--+
                   +--------+-------+
                   vyes             vno or unknown
              +---------+  +----------------+
              | success |  | failure record |
              +---------+  +----------------+
```

1. With `EventSelectionMode = selected`, the event `type` must be in
   `EventTypes`. `all` accepts every type.
2. A non-empty `Match` must also evaluate true.
3. Every matching subscription delivers independently. There is no first match
   or ordering, and no `Position` field.
4. Subscriptions are read at event time. Configuration changes apply to later
   events without a startup snapshot.

## Payload

The body is the CloudEvent serialized as UTF-8 JSON in structured content mode.
It uses the CloudEvents Published Language without a template engine.
Business-lineage extensions are top-level properties and are the authority for
business location. `data` remains the event payload.

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

## Authentication and Sending

```text literal
body   = rendered CloudEvent JSON as UTF-8 bytes
POST TargetUrl
  Content-Type: application/cloudevents+json
  Authorization: Bearer <token>           # AuthType=bearer
  Authorization: Basic <base64(user:pass)> # AuthType=basic
  <Custom-Header>: <value>                 # Each configured AuthType=custom header
  X-Hub-Signature-256: sha256=<hex>        # Compatibility only, with an old signature secret
```

- `Content-Type` is `application/cloudevents+json`.
- `none` adds no authentication header. `bearer` and `basic` add
  `Authorization`. `custom` adds each configured header.
- Custom headers cannot override `Host`, `Content-Length`, `Transfer-Encoding`,
  `Content-Type`, `X-Hub-Signature-256`, or `X-Mohist-*`.
- Only an old subscription with an HMAC secret adds
  `X-Hub-Signature-256`. A new subscription defaults to `none`; v1 exposes no
  signature secret on create.
- The request timeout is 15 seconds. A timeout records `request timed out`.

## Failure Behavior

Delivery is best effort. A non-2xx response, connection refusal, DNS error,
TLS error, or timeout writes a log and a durable failure record. The record
contains the subscription, target, event ID, available HTTP status, duration,
and error summary for Web and CLI inspection.

Failure does not retry automatically, block the event stream, or change Issue
or Workflow state. Delivery uncertainty remains visible. A 2xx response is success and writes no record.

Automatic retry, successful-delivery history, manual redelivery, test delivery,
and a Web management UI are outside v1.

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

The receiver parses the CloudEvent. Mohist does not define how the receiver
uses it.

## Status

The v1 contract is implemented: general HTTP delivery, `none`, bearer, basic,
and custom authentication; event selection with CEL filtering; CloudEvents
structured JSON; 2xx success; and durable failure records with status and
duration. Existing subscriptions may still use their HMAC secret.

V1 does not include test delivery, successful-delivery history, manual
redelivery, a Web management UI, automatic retry, or a Mohist-specific
signature protocol with key rotation.
