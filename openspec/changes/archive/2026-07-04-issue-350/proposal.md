## Why

Users are often away from the Mohist Web UI while an issue workflow continues in
autonomous mode. Three moments need direct attention or awareness in the chat
tool the user is already watching: an approval gate is waiting, the workflow has
failed, or the issue is complete.

Mohist already produces authoritative CloudEvents for those moments and projects
them into Web Inbox. This change adds a separate outbound Hermes webhook path so
those same moments can be pushed to Telegram, WeChat, or other Hermes-connected
chat platforms without replacing Web Inbox.

## What Changes

- Add server-side Hermes issue notification configuration under
  `Mohist:Notifications:Hermes`.
- Add a CloudEvent subscriber for approval requested, workflow failed, issue
  completed, and optional issue started events.
- Render type-specific message bodies in Mohist and include both the rendered
  body and raw fields in the webhook payload.
- Send JSON to the configured Hermes webhook URL with optional HMAC signature.
- Treat webhook delivery as best-effort: log and swallow delivery failures so
  workflow/issue execution is never blocked.
- Add Hermes template/subscription documentation.

## Capabilities

### New Capabilities

- `hermes-issue-notifications`: key issue workflow events can be sent to Hermes
  through a configured outbound webhook.

### Modified Capabilities

- None. Web Inbox behavior is unchanged.

## Impact

- **Server / Events**: new CloudEvent subscriber for issue notification moments.
- **Server / Notifications**: options, payload renderer, webhook client, and
  signature support.
- **Docs**: Hermes setup guide with template and payload example.
- **Tests**: focused unit specs for payload branches, defaults, filtering,
  disabled URL, delivery failure isolation, and signing.
