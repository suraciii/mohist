## Context

Mohist server is the control plane. It may publish state and event notifications,
but it must not start agent processes or shell commands. Hermes delivery to chat
platforms is therefore modeled as a network callback to a Hermes webhook
receiver, not as a server-side Hermes CLI invocation or local delivery daemon.

The existing `InboxProjectionHandler` already listens to the relevant
CloudEvents and writes persistent Web Inbox items. Hermes notifications are a
separate best-effort outbound path over the same event stream.

## Goals / Non-Goals

Goals:

- Send approval, failure, and completion notifications by default when a webhook
  URL is configured.
- Support start notifications but keep them disabled by default.
- Include issue number, title, stage/failure fields where relevant, suggested
  action, and pre-rendered body in the payload.
- Include the issue number in every suggested action.
- Optionally sign the JSON payload with a shared secret.
- Never block workflow or issue execution on Hermes delivery failure.

Non-goals:

- No server-side process launch or Hermes CLI calls.
- No retry queue, DLQ, delivery history, or reliability guarantee.
- No inbound chat handling in Mohist; Hermes plus the installed Mohist skill own
  natural-language replies.
- No change to Web Inbox behavior.

## Decisions

### D1. Bind configuration from `Mohist:Notifications:Hermes`

The minimal public contract is:

- `WebhookUrl`: missing or blank disables all outbound delivery.
- `Secret`: optional shared signing secret.
- `EnabledTypes`: notification kind allow-list.

The default enabled kinds are `approval_requested`, `workflow_failed`, and
`issue_completed`. `issue_started` is supported only when explicitly listed.

### D2. Render body in Mohist, format in Hermes

Hermes templates are kept simple and use `{{ body }}` plus optional metadata.
Mohist renders type-specific wording because it has the domain context and the
required branching. The payload carries both the rendered `body` and raw fields
for future Hermes-side formatting.

### D3. Resolve identity the same way Web Inbox does

Workflow events resolve issue identity from workflow run metadata annotations.
Issue events resolve identity from the CloudEvent extensions stamped by
`IssueGrain`. The loaded issue is then used to validate project/number and to
obtain the title snapshot.

### D4. Delivery is best-effort

The handler catches and logs webhook errors. A failed or unreachable Hermes
receiver cannot throw out of the event subscriber and cannot block workflow or
issue state changes.

## Risks / Trade-offs

- Best-effort delivery can drop a chat notification if Hermes is down. This
  matches the existing event bus semantics; Web Inbox remains the persistent
  notification record.
- `EnabledTypes` is an allow-list rather than per-kind booleans. This keeps the
  public config small while still supporting default-off `issue_started`.
