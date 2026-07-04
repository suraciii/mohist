# Hermes Issue Notifications

Mohist can send key issue workflow moments to Hermes through an outbound webhook.
Hermes remains responsible for delivering the message to Telegram, WeChat, or any
other connected chat platform.

## Mohist Configuration

Configure Mohist server in `~/.mohist/config.jsonc`:

```jsonc
{
  "Mohist": {
    "Notifications": {
      "Hermes": {
        "WebhookUrl": "https://hermes.example.com/webhooks/mohist",
        "Secret": "same-secret-as-hermes-subscription",
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

`WebhookUrl` is the Hermes webhook receiver URL. When it is missing or empty,
Mohist does not send anything.

`Secret` is optional. When set, Mohist adds `X-Mohist-Signature` with an
`sha256=<hex-hmac>` signature over the JSON request body.

`EnabledTypes` controls which notifications are sent. Defaults are:

- `approval_requested`
- `workflow_failed`
- `issue_completed`

`issue_started` is supported but off by default. Add it to `EnabledTypes` only
if start notifications are useful in your chat.

Restart the managed server after changing configuration:

```bash
mo update server
```

## Hermes Setup

1. Enable the Hermes webhook platform or HTTP webhook receiver.
2. Create a subscription whose target/source accepts Mohist webhook requests.
3. Set the Hermes subscription secret to the same value as
   `Mohist:Notifications:Hermes:Secret`.
4. Route the subscription output to the desired chat platform, such as Telegram
   or WeChat.
5. Use the template below as the Hermes message body.

Mohist renders the product message in `body`. Hermes only needs to format and
deliver it.

```text
{{ body }}

Source: Mohist
Project: {{ projectId }}
Event: {{ notificationType }}
```

## Payload Shape

Mohist posts JSON to `WebhookUrl`:

```json
{
  "notificationType": "approval_requested",
  "eventType": "com.mohist.workflow.stage.approval-requested",
  "sourceEventId": "evt_123",
  "occurredAt": "2026-07-03T12:01:00+00:00",
  "projectId": "proj_123",
  "issueId": "issue_123",
  "issueNumber": 42,
  "issueTitle": "Add Hermes outbound notifications",
  "workflowRunId": "wr_123",
  "stage": "plan",
  "failureReason": null,
  "suggestedAction": "approve 42",
  "body": "Issue #42 needs approval at stage plan: Add Hermes outbound notifications\nNext: approve 42"
}
```

Failure notifications include `failureReason` but do not include stack traces.
Every `suggestedAction` includes the issue number so a chat reply can target the
issue without relying on conversation context.
