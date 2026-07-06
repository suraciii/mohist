# Hermes Issue Notifications

Mohist can push key issue-workflow moments to your chat (Telegram, WeChat, …) by
handing them to [Hermes](https://github.com/) through an outbound webhook. Hermes
owns the actual delivery to the chat platform; Mohist only renders the message
body and posts it.

The split is deliberate: Mohist's server never spawns processes (it is the state
and decision plane), so it talks to Hermes over HTTP — the same kind of outlet it
already uses for the Web UI — and Hermes does the chat-side work.

## When notifications fire

Four moments, three on by default:

| Moment | Trigger event | Default |
|--------|---------------|---------|
| Approval gate reached | `workflow.stage.approval-requested` | on |
| Workflow failed | `workflow.run.failed` | on |
| Issue completed | `issue.completed` | on |
| Issue started | `issue.work-started` | **off** (noise for an issue you just opened) |

Each message carries: what happened, which issue (number + title), and a suggested
next action that always includes the issue number (e.g. `approve 42`) so a chat
reply can target the issue without conversation context. Failure notices do **not**
include stack traces.

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

- `WebhookUrl` — Hermes webhook receiver URL. When missing/empty, Mohist sends nothing.
- `Secret` — optional. When set, Mohist adds an `X-Hub-Signature-256: sha256=<hex-hmac>` header over the JSON body (the GitHub-style header Hermes validates). This must match the **subscription-level** secret on the Hermes side (see below).
- `EnabledTypes` — which moments to send. Add `issue_started` if start pings are useful.

After changing this config, reload the managed server:

```bash
mo update server
```

> Note: the notification config is a nested section. `mo config get/set/list` only
> covers flat keys today; edit `~/.mohist/config.jsonc` directly for this section.
> (Tracked as a follow-up to unify the config surface.)

You can also bootstrap a first config with the guided command:

```bash
mo notification setup --platform telegram
# For a platform without a default home channel (e.g. weixin):
mo notification setup --platform weixin --deliver-chat-id "<your-weixin-chat-id>"
```

It probes the Hermes webhook port, generates a shared secret, writes the Mohist
section above, and prints the matching `hermes webhook subscribe` command to run
(with the chat id folded in when supplied). See `mo notification setup --help` for flags.

## Hermes Setup

These steps live on the Hermes side. They are Hermes' own commands; Mohist does
not modify Hermes config.

### 1. Enable the Hermes webhook platform

Add a top-level `platforms.webhook` block to `~/.hermes/config.yaml` (it must be
top-level, not under `gateway.platforms`):

```yaml
platforms:
  webhook:
    enabled: true
    extra:
      host: "127.0.0.1"   # Mohist is on the same host
      port: 8644
      secret: "<any-strong-random-string>"   # platform-level, see note below
```

Restart the gateway:

```bash
hermes gateway restart
```

Verify it is listening:

```bash
curl http://127.0.0.1:8644/health
# {"status": "ok", "platform": "webhook"}
```

> **Two layers of secret — don't confuse them.** The `platforms.webhook.extra.secret`
> above is platform-level. The subscription you create next gets its own
> **subscription-level** secret (the `--secret` passed to `hermes webhook subscribe`).
> Hermes validates incoming POSTs against the **subscription-level** secret. Mohist's
> `Secret` (above) must match the subscription-level one, not the platform-level one.

### 2. Create the Mohist subscription

Use the **subscription-level** secret (the same value you put in Mohist's `Secret`).
The `--prompt` template is kept minimal because Mohist already renders the message
body — Hermes only passes it through:

```bash
hermes webhook subscribe mohist \
  --deliver telegram \
  --deliver-only \
  --secret "<same-secret-as-Mohist>" \
  --prompt '{body}'
```

- `--deliver-only` — skip the agent loop; deliver the rendered template verbatim. Zero LLM cost.
- `--prompt '{body}'` — Hermes' template syntax is single-brace `{field}` placeholders
  referencing the POST body's fields. Mohist renders the full message into `body`, so a
  bare `{body}` is all you need. (Other available fields: `{issueNumber}`, `{issueTitle}`,
  `{notificationType}`, `{stage}`, `{suggestedAction}`, … — but prefer `{body}` unless you
  want a custom layout.)
- `--deliver <platform>` — the chat platform to push to.

### 3. Platforms that need a chat id

Telegram has a default home channel, so `--deliver telegram` alone is enough. **WeChat
(weixin) does not** — it needs an explicit chat id:

```bash
hermes webhook subscribe mohist \
  --deliver weixin \
  --deliver-chat-id "<your-weixin-chat-id>" \
  --deliver-only \
  --secret "<same-secret-as-Mohist>" \
  --prompt '{body}'
```

Find your chat id with:

```bash
hermes send --list weixin
```

### 4. Verify

Send a signed test POST that mimics Mohist's outbound payload:

```bash
curl -X POST http://127.0.0.1:8644/webhooks/mohist \
  -H "Content-Type: application/json" \
  -H "X-Hub-Signature-256: sha256=<hmac>" \
  -d '{"body":"Mohist notification link verified.","issueNumber":0}'
```

A `{"status":"delivered"}` response means the full link works. (Computing the HMAC by
hand is awkward; the easiest end-to-end check is to drive a real issue to an approval
gate / completion and watch the chat.)

## WeChat customer-service window (important limitation)

WeChat (via iLink) only lets a bot push messages within a limited window after the user
last messaged it (roughly 48h in practice). If you have not messaged the bot from WeChat
for a while, outbound notifications silently fail with `ret=-2`, which Hermes reports as
"rate limited" (misleading — it is a window expiry, not a rate limit).

This conflicts with the highest-value notification — *issue completed* — which tends to
fire long after the user walked away. **Prefer Telegram as the default notification
channel.** Treat WeChat as a secondary channel that works while you are actively in the
conversation.

To revive a WeChat window: send the bot any message (e.g. `hi`) from WeChat, then
notifications will deliver again until the window lapses.

## Payload Shape

Mohist posts JSON to `WebhookUrl` (camelCase, per CloudEvents/web convention):

```json
{
  "notificationType": "approval_requested",
  "eventType": "com.mohist.workflow.stage.approval-requested",
  "sourceEventId": "evt_123",
  "occurredAt": "2026-07-03T12:01:00+00:00",
  "projectId": "proj_123",
  "issueId": "issue_123",
  "issueNumber": 42,
  "issueTitle": "Add login rate limiting",
  "workflowRunId": "wr_123",
  "stage": "plan",
  "failureReason": null,
  "suggestedAction": "approve 42",
  "body": "Issue #42 在 plan 阶段等你审批。下一步:approve 42"
}
```

`body` is the pre-rendered message text (with per-kind wording, in Mohist's configured
language). `failureReason` is set only for `workflow_failed`; it carries a short reason,
never a stack trace. `suggestedAction` always carries the issue number.

The `body` field is the one a default Hermes `{body}` template consumes; the other fields
are available for custom templates or future channels.

## Reliability

Delivery is best-effort and matches the in-process event bus semantics: a webhook failure
(non-200, connection refused, …) is logged and swallowed — it never blocks or retries, and
it never affects issue / workflow execution. There is no outbox, retry queue, or DLQ.

For a durable record of what happened, the Web Inbox remains the source of truth; this
webhook is a transient push, not a persistent log.
