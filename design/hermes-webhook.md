# Hermes Webhook

Mohist sends important Issue Workflow events to Hermes through an outbound
webhook. Hermes performs the actual delivery to chat platforms such as
Telegram and Weixin. See
[`docs/hermes-notifications.md`](../docs/hermes-notifications.md) for product
semantics, notification moments, and enablement. This document owns protocol
and configuration details.

The division is deliberate. Mohist Server is the state and arbitration plane
and never spawns a process. It renders the message body and sends an HTTP POST
to Hermes. Hermes owns everything on the chat side.

## Event Types

See [`docs/hermes-notifications.md`](../docs/hermes-notifications.md) for the
product meaning and default enablement of each notification moment. Wire
`notificationType` corresponds to the triggering event:

| notificationType | Triggering event |
|---|---|
| `approval_requested` | `workflow.stage.approval-requested` |
| `workflow_failed` | `workflow.run.failed` |
| `issue_completed` | `issue.completed` |
| `issue_started` | `issue.work-started` |
| `agent_response_failed` | `agent.job.failed` |

## Mohist Configuration

Configure the outbound webhook in `~/.mohist/config.jsonc`:

```jsonc
{
  "Mohist": {
    "Notifications": {
      "Hermes": {
        "WebhookUrl": "http://127.0.0.1:8644/webhooks/mohist",
        "Secret": "the-shared-secret",
        "EnabledTypes": [
          "approval_requested",
          "workflow_failed",
          "issue_completed"
        ]
      }
    }
  }
}
```

- `WebhookUrl`: Hermes webhook receiver URL. When missing or empty, Mohist
  sends nothing.
- `Secret`: Optional. When set, Mohist computes an HMAC over the JSON body and
  adds `X-Hub-Signature-256: sha256=<hex-hmac>`, which Hermes verifies in the
  GitHub style. It must equal the Hermes **subscription-level** secret; see Two
  Secret Levels below.
- `EnabledTypes`: Notification moments to send. Include `issue_started` for a
  start notification.

Reload the managed service after changing configuration:

```bash
mo update server
```

> Notification configuration is a nested section and currently requires direct
> editing of `~/.mohist/config.jsonc`. A unified configuration command remains
> follow-up work.

The setup guide can generate the initial configuration:

```bash
mo notification setup --platform telegram
# For a platform without a default home channel, such as Weixin:
mo notification setup --platform weixin --deliver-chat-id "<your-weixin-chat-id>"
```

The guide probes the Hermes webhook port, generates a shared secret, writes the
Mohist section above, and prints the matching `hermes webhook subscribe`
command. A provided chat ID is included in that command. See
`mo notification setup --help` for flags.

## Configure Hermes

The following commands and configuration belong to Hermes. Mohist does not
modify Hermes configuration.

### 1. Enable the Hermes Webhook Platform

Add a **top-level** `platforms.webhook` block to `~/.hermes/config.yaml`. It must
not be nested under `gateway.platforms`:

```yaml
platforms:
  webhook:
    enabled: true
    extra:
      host: "127.0.0.1"   # Mohist and Hermes run on the same host
      port: 8644
      secret: "<any-strong-random-string>"   # Platform-level; see below
```

Restart the gateway and verify the listener:

```bash
hermes gateway restart
curl http://127.0.0.1:8644/health
# {"status": "ok", "platform": "webhook"}
```

> **Two Secret Levels:** `platforms.webhook.extra.secret` above is the
> **platform-level** secret. The subscription created next has its own
> **subscription-level** secret through `hermes webhook subscribe --secret`.
> Hermes verifies inbound POSTs with the subscription-level secret. Mohist
> `Secret` must match that value, not the platform-level secret.

### 2. Create the Mohist Subscription

Use a subscription-level secret equal to Mohist `Secret`. Keep the `--prompt`
template minimal because Mohist already rendered the body and Hermes only
delivers it:

```bash
hermes webhook subscribe mohist \
  --deliver telegram \
  --deliver-only \
  --secret "<same-secret-as-Mohist>" \
  --prompt '{body}'
```

- `--deliver-only`: Skips the Agent loop and delivers the rendered template
  unchanged, with no LLM cost.
- `--prompt '{body}'`: Hermes templates use single-brace `{field}` placeholders
  for POST body fields. Mohist renders the complete message into `body`, so
  `{body}` is sufficient. Other available fields include `{issueNumber}`,
  `{issueTitle}`, `{notificationType}`, `{stage}`, and `{suggestedAction}`. Use
  `{body}` unless custom layout is necessary.
- `--deliver <platform>`: Selects the chat platform.

### 3. Platforms That Require a Chat ID

Telegram has a default home channel, so `--deliver telegram` is sufficient.
**Weixin does not**, and requires an explicit chat ID:

```bash
hermes webhook subscribe mohist \
  --deliver weixin \
  --deliver-chat-id "<your-weixin-chat-id>" \
  --deliver-only \
  --secret "<same-secret-as-Mohist>" \
  --prompt '{body}'
```

List chat IDs with:

```bash
hermes send --list weixin
```

### 4. Verify

Send a signed test POST that represents a Mohist outbound payload:

```bash
curl -X POST http://127.0.0.1:8644/webhooks/mohist \
  -H "Content-Type: application/json" \
  -H "X-Hub-Signature-256: sha256=<hmac>" \
  -d '{"body":"Mohist notification link verified.","issueNumber":0}'
```

`{"status":"delivered"}` confirms the path. Rather than calculate HMAC
manually, the simplest end-to-end check advances a real Issue to an approval
point or completion and observes delivery in the chat tool.

## Payload

Mohist POSTs camelCase JSON to `WebhookUrl`, following CloudEvents and Web
conventions:

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
  "failureReason": null,
  "suggestedAction": "approve 42",
  "body": "Issue #42 is waiting for an approval decision in plan. Next: approve 42"
}
```

- `body`: Prerendered message text using wording for the notification type and
  the language configured in Mohist. The default Hermes `{body}` template uses
  it. Other fields support custom templates and future channels.
- `epicNumber`: Omitted when the Issue currently belongs to no Epic.
- `failureReason`: Present only for `workflow_failed`, contains a short reason,
  and never includes a stack trace.
- `suggestedAction`: Always contains the Issue number.

## Signature Verification

When `Secret` is set, Mohist computes HMAC-SHA256 over the JSON body and sends
the GitHub-style `X-Hub-Signature-256: sha256=<hex-hmac>` header that Hermes
verifies. Verification uses the **subscription-level** secret from
`hermes webhook subscribe --secret`, independently of the platform secret.

## Weixin Customer-service Window

Through iLink, Weixin permits a Bot to push only within a limited window after
the user's latest message, about 48 hours in practice. After expiry, outbound
notification fails silently with `ret=-2`. Hermes reports this as "rate
limited," although the window expired rather than a rate limit being reached.

This conflicts with the high-value Issue-completion notification, which often
occurs after the user has been away. **Prefer Telegram as the default
notification channel** and use Weixin during an active conversation. Any
message to the Bot, such as `hi`, reopens the window.

## Delivery Reliability

The event bus is authoritative for delivery to the subscription handler. See
[`eventbus.md`](eventbus.md) for at-least-once delivery, Polly retries, and DLQ
behavior.

The Hermes-specific last hop, the outbound HTTP POST, is best effort. A failure
such as non-200 or connection refusal is logged and consumed without retry. It
never blocks or changes Issue or Workflow execution. This hop has no separate
outbox, retry queue, or DLQ.

Web Inbox is the durable source for what happened. A webhook is an immediate
notification, not a durable log.

## Source Locations

Options, renderer, payload, and `HermesWebhookClient` are in
`packages/server/src/Mohist.Server/Notifications/`. The subscription entry is
`packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs`.
The CLI guide is in `packages/cli/Mohist.Cli/MohistCliCommands.Notify.cs`.
