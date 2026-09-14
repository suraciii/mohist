# Runtime Switch Context

Mohist keeps a logical Agent Session alive even when its runtime-specific
physical session is unavailable. The runtime may therefore be replaced without
creating a new logical session or a synthetic user turn.

## Design Drivers

Physical runtime history is disposable; canonical Session and Slack history are
durable. Copying a full transcript is expensive, provider-specific, and can
reintroduce stale runtime state. A new runtime must still know which logical
Session and workspace it serves. Buzz uses a stable system prompt plus
channel/thread context and exposes history commands; it does not send a fake
handoff user message for runtime replacement.

## Model

`AgentSessionId` is the stable logical identity. `runtimeSessionId` identifies a
provider's physical session and may be replaced. A replacement increments the
binding generation and records the new runtime and physical identity.

The replacement context is system/infrastructure context, not a user Input or
Turn. It contains the canonical Session ID, workspace, and source conversation
identity. The agent may use the existing bounded Session transcript/view
command (`mo session transcript <session-id>`); Slack history injection or a
new Slack history command is out of scope until separately specified.

## Semantics

When a bound runtime is unavailable, the Runner may select the configured
fallback runtime (Pi), create an empty physical session, and
atomically replace the binding. It then sends the original user input exactly
once to the new runtime. It must not create a new logical Session, replay prior
inputs, or emit a synthetic handoff user message.

The new runtime receives the standard system context and can request the
canonical Session transcript on demand through the existing read-only command.
History is bounded, ordered, and redacted. The system does not inject a full
transcript by default.

If replacement or binding CAS fails, the original binding and canonical history
remain unchanged and the Turn is reported as retryable/unavailable. A failed
replacement must not leave a partially adopted physical session as current.

```text diagram
old binding unavailable
  -> create fallback physical session
  -> persist (logical Session unchanged, runtime binding replaced)
  -> send original input once
  -> agent reads history only if needed
```

## Non-Goals

This does not migrate provider conversation files, summarize history, create a
new queue, or automatically repair a provider whose completion signal is broken.

## Status

This specification replaces the earlier proposal for a one-time user-facing
handoff prompt. The runner fallback and focused recovery tests are implemented;
full Server gate and live Slack runtime-switch acceptance remain pending.
