## Why

Slack currently provides a signed Stop action for active work, but a failed result is effectively a text-only recovery path: the user must reconstruct and resend the request to try again. When multiple enabled Mohist Bots are mentioned, the once-only prompt likewise asks the user to mention one Bot again instead of offering a direct choice. The existing signed interaction endpoint, Slack Block Kit delivery, and durable inbox/outbox primitives make this the right time to add actionable recovery and attribution without weakening authorization or idempotency.

## What Changes

- Add an expiring, signed, actor-bound `Retry` action to retryable failed Slack results. A valid action starts a fresh execution attempt from the original Slack request/session context and reports whether the retry was accepted, already applied, stale, or unavailable.
- Ensure expired, tampered, unauthorized, stale, and replayed Retry actions have no runtime side effect and produce an explicit Slack response. Retry dispatch and status updates remain idempotent across Slack redelivery and adapter failover.
- Replace the text-only multi-Bot ambiguity prompt with a signed interactive choice containing one action per eligible mentioned Bot. Selecting a Bot dispatches the original message to that selected Connection in its original conversation/thread; no unselected Bot starts work.
- Persist the stable ambiguous-message identity, candidate set, and selection outcome needed to reject stale candidates and collapse concurrent or repeated selections to one result. Keep a readable text fallback when interactive actions are unavailable.
- Extend the server interaction/action and Slack adapter delivery contracts to carry the new Block Kit actions and result states, while preserving the existing signed Stop action behavior.

## Capabilities

- `slack-failure-retry-action`: Signed, authorized Retry controls for failed Slack work, including fresh-attempt dispatch, terminal-state validation, idempotency, replay handling, and user-visible outcomes.
- `slack-multi-bot-selection`: Signed interactive selection for ambiguous multi-Bot messages, including candidate validation, original-message routing, single-winner persistence, and stale/replayed selection handling.

## Impact

- **Server Slack interaction and routing:** `SlackTurnControlService`, `SlackInteractionRoutes`, and `SlackConnectionRoutes` will gain new action payloads, signature verification/authorization branches, retry dispatch, and selected-Bot routing.
- **Slack result presentation:** terminal/failure rendering, status projection, and Slack outbox payloads will carry action blocks and replace or acknowledge them after a selection or Retry attempt.
- **Durable state:** `SlackAmbiguousPromptStore` and its database row/migration will need to retain enough candidate and selection/source state for deterministic multi-Bot handling. Existing provider inbox/outbox deduplication remains part of the control path.
- **Adapter contract:** `packages/mohist-slack` interaction normalization and transport types/tests must forward the new signed Block Kit actions; the adapter continues to acknowledge Slack promptly and delegates authorization to the Server.
- **Security and dependencies:** HMAC signing remains based on the verified Slack Connection credential, with expiry, workspace/conversation/actor checks, and constant-time verification. The change should use the existing Slack Block Kit, database, and outbox infrastructure and introduce no new third-party dependency.
- **Verification:** server integration/unit coverage and adapter contract coverage will be extended for successful, rejected, expired, stale, concurrent, and replayed actions.
