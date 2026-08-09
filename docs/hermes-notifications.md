# Hermes Notifications

Mohist supports long-running self-hosted work. After an Issue starts, its
Workflow can run for minutes or hours, and the user should not need to watch the
screen. Some moments still require attention: an Approval point is reached,
execution fails, or the Issue finishes. Hermes notifications send these moments
to a chat platform such as Telegram or WeChat. The user can see them on a phone
and respond with a command so work can continue.

## Mental Model

- **Mohist decides when and what to notify:** It observes the Workflow and
  prepares a message at important moments.
- **Hermes delivers:** Mohist gives the message to Hermes, which sends it to a
  specific chat platform. Mohist does not integrate directly with chat
  platforms.
- A notification is an **immediate delivery, not a durable record**. Missed
  notifications are not replayed. The Web inbox is the source of truth for
  complete history.
- Notifications **never interfere with work**. A delivery failure is recorded
  and abandoned. It never blocks or changes Issue or Workflow execution.

## Notification Events

Four of the five event types are enabled by default:

| Event | Default |
|---|---|
| An Approval point is waiting for a decision | On |
| The Workflow failed and blocked the Issue | On |
| The Issue completed | On |
| An Agent response failed to handle its work | On |
| The Issue started work | **Off**, because the user usually started it and the notice is noise |

Each notification says what happened, identifies the Issue by number and title,
and suggests a next action. A suggested command always includes the Issue
number, such as `approve 42`, so a chat response needs no conversational
context. Failure notifications contain a short reason, not a stack trace.

## Setup

### Enable the Hermes Webhook Listener

Hermes must expose its webhook platform before Mohist setup can create a
subscription. Add a top-level `platforms.webhook` block to
`~/.hermes/config.yaml`; do not place it under `gateway.platforms`:

```yaml
platforms:
  webhook:
    enabled: true
    extra:
      host: "127.0.0.1"
      port: 8644
      secret: "<platform-level secret>"
```

Restart Hermes and check the listener:

```bash
hermes gateway restart
curl http://127.0.0.1:8644/health
```

The listener secret above protects the whole Hermes webhook platform. It is
not the secret Mohist uses. Each subscription has a separate secret so one
sender does not receive platform-wide trust.

### Connect Mohist

One guided command configures the Mohist side:

```bash
mo notification setup --platform telegram
```

The guide checks the local listener, generates a subscription-level secret,
writes Mohist notification configuration, and prints the matching Hermes
subscription command. Run that printed command to complete the connection. It
has this shape:

```bash
hermes webhook subscribe mohist \
  --deliver telegram \
  --deliver-only \
  --secret "<subscription-level secret printed by setup>" \
  --prompt '{body}'
```

`--deliver-only` is required because Mohist has already rendered the complete
message. It prevents an Agent or LLM from reinterpreting the notification.
Mohist signs each request with the subscription-level secret; do not substitute
the platform-level secret from `config.yaml`. See
`mo notification setup --help` for all options.

WeChat has no default receiving chat. Specify its chat ID explicitly. Use
`hermes send --list weixin` to find one.

```bash
mo notification setup --platform weixin --deliver-chat-id "<your WeChat chat ID>"
```

Reload Server after writing the configuration:

```bash
mo update server
```

For a direct end-to-end check, drive a real Issue to an Approval point or
completion and verify that the chat platform receives the notification.

If setup cannot reach Hermes, first check `hermes gateway status` and the health
URL above. If Hermes accepts a request but no message arrives, confirm that the
subscription uses `--deliver-only`, its `--secret` matches the Mohist-generated
subscription secret, and platforms without a home channel have an explicit
`--deliver-chat-id`.

## Failure Recovery

Suggested actions in failure notifications map directly to recovery commands.
After seeing that Issue 42 failed, run `mo run retry --issue 42` in any terminal,
or ask an Agent in chat to do so. An Approval notification similarly maps to
`mo run approve --issue 42` or
`mo run reject --issue 42 --message "describe the required changes"`. See
[Troubleshooting](troubleshooting.md) for the complete recovery map.

## WeChat Delivery Window

WeChat permits a bot to push messages only for a limited period after the user
last sent it a message, approximately 48 hours in practice. Delivery fails
silently after the window closes. This conflicts with high-value Issue
completion notifications, which often occur long after the user leaves.
Telegram is therefore the recommended default notification channel. Use WeChat
as a secondary channel during an active conversation. Sending any message,
such as `hi`, reopens the window until it expires again.

## Implementation Gaps

- There is no command surface for per-event toggles, such as enabling the
  default-off Issue-started notification. For now, rerun the guide or edit
  Server configuration manually. A unified command surface remains future
  work.

See [`design/hermes-webhook.md`](../design/hermes-webhook.md) for the authority,
wire, signature, and failure-isolation contracts.
