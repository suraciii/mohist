## Why

Pi Workflow turns already execute end-to-end (#450), but a Pi-bound AgentSession is read-only in the Session page: the server blocks all four session commands for the `pi` runtime, and the runner's live follow-up/cancel handlers hardcode `opencode`. Users watching a Pi conversation cannot follow up, compact, reset, or cancel it the way they can on an OpenCode session. The command contract, concurrency boundary, and stable-identity model are already settled and shipped (#407), and the Pi channel mapping is designed (`design/runtimes/pi.md` «Session 命令»); this issue closes the gap by routing the existing commands to `PiRuntime` and implementing the Pi-side channels. It does not add new command types or change OpenCode behavior.

## What Changes

- Lift the server-side Pi gate in `AgentSessionGrain.EnsureCommandRuntimeAvailable` so Compact, Reset, Follow-up, and Cancel are admitted for `pi`-bound sessions under the same idle/concurrency, expected-binding, and stable-identity rules as OpenCode.
- Make the runner's live follow-up/cancel handlers runtime-aware: `followup-handler` and `cancel-handler` dispatch on the binding's `runtime` instead of hardcoding `opencode`; `RunnerSignalRClient` and `RunnerHost` thread a `PiRuntime` accessor next to the `OpenCodeRuntime` one; `resolveFollowupTarget` admits Pi bindings.
- Bring the runner `SessionCommand` handler online for Pi: wire `registerSessionCommandHandler` into `RunnerSignalRClient.registerHandlers` with a runtime-routing handler function that directs Pi targets to `PiRuntime.compact` / `PiRuntime.reset`. OpenCode compact/reset keeps its current path and behavior; no runner-side OpenCode compact/reset is introduced.
- Add `followup()`, `cancel()`, `compact()`, and `reset()` methods to `PiRuntime` realizing the design's Pi channel mapping: `session.steer()` for in-turn follow-up; `session.prompt({ preflight })` for idle follow-up with Pi reception confirmation (preflight rejection = command failure); `session.compact()` for native compaction (no synthetic-summary fallback); `SessionManager.create(cwd)` for reset with current model/thinking-level carry-over, binding replacement, and lineage append; `session.abort()` with `isStreaming`/event-sequence stop-confirmation for cancel.
- Extend the `PiSdkSession` boundary interface with `compact()` so the deep-module boundary tracks the SDK surface the design requires, and add the corresponding request/result boundary types mirroring the OpenCode shapes.
- Keep the AgentSession ID stable across all four commands; a missing Pi session file fails explicitly with a Reset hint (no silent new session); cancel reports "interrupt unconfirmed" when stop cannot be confirmed, never portraying a possibly-still-running turn as safely stopped.

## Capabilities
<!-- Each capability gets a specs/<name>/spec.md describing required behavior. -->
- `pi-session-command-routing`: Runtime-aware dispatch of the four session commands to the bound runtime's handler. The server admits `pi` under the existing contract; runner follow-up, cancel, and `SessionCommand` handlers route by `binding.runtime`; OpenCode command behavior and the existing `SessionCommand` wire/contract are unchanged.
- `pi-session-channels`: `PiRuntime`'s implementation of the four commands via the Pi SDK operations defined in `design/runtimes/pi.md` — Follow-up (steer while busy, prompt+preflight while idle), Compact (native `session.compact()`), Reset (new session file + lineage, no context migration), and Cancel (`abort()` + stop confirmation) — including missing-session → Reset hint, reception-confirmed idle follow-up, and interrupt-unconfirmed cancel.

## Impact

- **Server** (`packages/server/src/Mohist.Server/`): `Sessions/Grains/AgentSessionGrain.cs` — `EnsureCommandRuntimeAvailable` admits `pi`. No contract change to `RunnerSessionCommandDispatcher`, `AgentSessionFollowupRoutes`, `AgentSessionCancelRoutes`, or `AgentSessionRecoveryRoutes`: they already stamp `runtime` on the wire payload from the AgentSession binding, so Pi flows through them once the grain gate lifts.
- **Runner** (`packages/runner/src/`):
  - `server/runner-signalr.ts`, `runtime/host.ts` — pass a `PiRuntime` accessor into the SignalR client; admit Pi in `resolveFollowupTarget`.
  - `server/followup-handler.ts`, `server/cancel-handler.ts` — dispatch on `target.runtime`; call `PiRuntime.followup` / `PiRuntime.cancel` for Pi.
  - `server/session-command-handler.ts` (+ `runtime/session-command-journal.ts`) — wire the existing-but-unwired compact/reset handler into `registerHandlers`, with a router that targets `PiRuntime.compact` / `PiRuntime.reset` for `pi` bindings.
  - `runtime/pi/runtime.ts` — add `followup`, `cancel`, `compact`, `reset`; `runtime/pi/sdk.ts` — extend `PiSdkSession` with `compact`; `runtime/pi/types.ts` — add request/result boundary types.
- **CLI / Web**: no contract change — the command surface and Session page are already runtime-neutral; Pi commands become callable through the existing entry points.
- **Docs**: remove the "Pi Session 命令仍未实装" gap entries in `docs/actions/pi.md` and `design/runtimes/pi.md` once landed.
- **Tests**: runner handler-level routing tests (Pi target → PiRuntime, OpenCode target unchanged), `PiRuntime` unit tests per command via a fake SDK (steer vs prompt+preflight, native compact, reset creates new file + lineage, abort + isStreaming confirmation, missing-session → Reset hint, interrupt-unconfirmed), and server spec tests covering Pi admitted through compact/reset/followup/cancel with idle-conflict and missing-binding paths. No real Pi, process, network, database file, or wall clock.
- **Risk (medium)**: touches the Server→Runner command channel and dispatch into a second execution backend. Mitigated by reusing the settled #407 contract (no new semantics), keeping OpenCode paths unchanged, and routing strictly off the persisted `binding.runtime` already on the wire.
