## Why

When a Runner restarts (e.g., a Node.js out-of-memory exit), every active AgentSession bound to it flips to `unknown`, and nothing reconciles them on reconnect. The freshly started Runner process has no knowledge of prior Runtime Session ids, so a still-queryable OpenCode Session is reported missing; confirmed-missing recovery then fails with `agent_session_recovery_conflict` because it requires `idle`; and a confirmed Cancel never writes back an activity fact, so the session stays stuck until an operator manually injects a runtime event — after which retry replaces the still-available session and loses its context. The violated invariant is that a Runtime Session still existing on its owning Runner must not be reported missing or replaced, and `unknown` must never be treated as a safe `idle`.

## What Changes

- Add Runner-reconnect reconciliation: when a Runner reconnects, the Server reconciles each AgentSession bound to it against the physical Runtime Session via the owning Runner's deterministic existence check and active-turn snapshot. A still-existing session with no active turn keeps its current binding and settles to `idle`; a confirmed-missing session authorizes the existing confirmed-missing recovery; transient or unclassifiable results leave the session `unknown` and preserve the binding.
- Make a confirmed Cancel settle AgentSession activity to `idle` through the normal API and CLI, using a binding-guarded transition — with no operator-written runtime event.
- Preserve a still-queryable physical Runtime Session across task start, retry, Follow-up, Compact, Cancel, and reconnect: it is never classified as missing or replaced, and the resolve step returns ready with the binding unchanged.
- Route every physical Session existence check through the official, type-checked OpenCode SDK request contract so SDK DTO drift cannot hide a misclassification.
- Keep every non-recovery condition an explicit failure that preserves the binding and never replays input: timeout, transport failure, unavailable runtime, corrupt response, uncertain input acceptance, `active`, and unresolved `unknown`. `unknown` is never simplified to `idle`.

## Capabilities

- `agent-session-activity` (modifies): A confirmed Cancel produces the `active + execution confirmed stopped -> idle` transition observably through the normal API/CLI, binding-guarded, with no operator-written runtime event; the sanctioned `unknown + runtime evidence -> idle` transition is produced by reconnect reconciliation rather than only by incidental runtime events or manual repair.
- `runner-reconnect-reconciliation` (new): On Runner reconnect, reconcile each bound AgentSession against the physical Runtime Session on the owning Runner — preserve a still-existing session with no active turn and settle it to `idle`; trigger confirmed-missing recovery only when the owning Runner confirms absence; preserve the binding and keep `unknown` on transient or unclassifiable results; reject facts from a superseded binding.
- `runtime-binding-recovery` (modifies): A still-queryable physical Runtime Session is never classified as missing or replaced during task start, retry, Follow-up, Compact, Cancel, or reconnect; only a confirmed-missing result from the owning Runner authorizes recovery; reconnect joins task and idle-Follow-up input as a sanctioned recovery trigger.

## Impact

- **Server domain & grain** (`packages/server/src/Mohist.Server/Sessions/`): add a reconnect counterpart to `RunnerDisconnectedAsync` (`AgentSessionGrain.cs`) that probes and reconciles binding and activity; add a binding-guarded cancel-settles-idle transition; extend `RebindRuntimeSession`/recovery to the reconnect-driven confirmed-missing path.
- **Server SignalR** (`Runner/Services/SignalR/RunnerHub.cs`, `RunnerConnectionTracker.cs`): `OnConnectedAsync` enumerates sessions bound to the reconnecting Runner and triggers reconciliation.
- **Server API** (`Api/AgentSessionCancelRoutes.cs`, `RunnerRoutes.cs`): the cancel route records the confirmed-stopped activity fact; the recovery routes surface the reconnect-driven path.
- **Runner** (`packages/runner/src/runtime/`): reconnect convergence (`runtime/host.ts`, `cleanup-convergence.ts`) gains AgentSession binding reconciliation; `binding-recovery.ts` and the OpenCode/Pi adapters route existence checks through the typed SDK contract (`opencode/runtime.ts`, `pi/runtime.ts`).
- **Cancel handler** (`server/cancel-handler.ts`): reports the confirmed-stopped fact back through the durable outbox so the grain settles activity.
- **Tests** (`packages/server/tests/...`, `packages/runner/`): reconnect reconciliation, cancel-settles-idle, still-queryable-never-replaced, confirmed-missing-on-reconnect creating at most one candidate, and the exhaustive non-recovery set — using injected fakes and no wall-clock timing.
