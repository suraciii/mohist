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

One guided command configures the Mohist side:

```bash
mo notification setup --platform telegram
```

The guide detects local Hermes, generates a shared secret, writes Mohist
notification configuration, and prints the subscription command to run in
Hermes. Run that command to complete the connection. See
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

---

Source: `packages/server/src/Mohist.Server/Notifications/`; CLI:
`packages/cli/Mohist.Cli/MohistCliCommands.Notify.cs`. See
[`design/hermes-webhook.md`](../design/hermes-webhook.md) for protocol,
configuration keys, and Hermes integration details.
