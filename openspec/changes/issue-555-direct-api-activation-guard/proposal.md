## Why

The first concrete `/api/v1` surface is the Job-only public read. The rest of
the External Agent API still needs a durable execution projection and
caller-scoped idempotency records. Mapping those future route shapes before
those owners exist would make an external request look available without a
recoverable execution identity.

## Change

Record the activation boundary for the current Job-only read and add a
negative contract for the future launch, follow-up, stop, Input/Turn read, and
Session event routes. Until their durable owners are implemented, each future
route remains unreachable and cannot create a Job, Session, Input, Turn,
idempotency record, public event, or external effect.

The existing Job read remains the only mapped direct route. It continues to
require a Bearer PAT with an explicit Project grant and returns only its
persisted allowlisted projection or `projection_lag`.

## Non-goals

- Implementing launch, follow-up, stop, Input/Turn reads, or Session events.
- Reusing the control-plane AgentLaunchCoordinator as a direct API idempotency
  store; its `(ProjectId, Idempotency-Key)` scope has no caller binding.
- Adding a placeholder `501` endpoint or a fallback to canonical Job/Session
  state.
