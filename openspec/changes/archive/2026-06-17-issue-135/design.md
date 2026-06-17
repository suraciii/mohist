## Context

`monitorPrompt` (`packages/runner/src/actions/acp-agent.ts`) drives a single opencode prompt to completion and is the heart of every agent-session task. It can terminate through five paths:

1. **completed** — the prompt resolved normally.
2. **timeout** (`timeoutRemaining <= 0`) — the prompt's `timeoutMs` budget ran out.
3. **abort** — the runner's abort signal fired (user stop).
4. **protocol/process error** — `promptOutcome`/`exitFailure` rejected.
5. **liveness probe failure** — quiet threshold → probe → probe timeout / send-failure.

Paths 4 and 5 (and the probe-abort sub-path of 3) all do the same three things: query the opencode log via `findOpencodeProviderErrorDiagnostic(sessionId)`, emit a `failed` liveness status event carrying that diagnostic, and return the diagnostic appended to the error. The **timeout** path does none of them — it calls `cancelAndReturn(connection, sessionId, "Timed out after …s")` and returns a bare string (Bug #1).

`cancelAndReturn` awaits `connection.cancel({ sessionId })` with no bound. `connection.cancel` writes to the opencode process's stdin over an ordered nd-json stream and awaits a protocol response. When the opencode process is hung mid-LLM-stream, the cancel request queues behind the hung request and is never serviced — production issue #126 / T-003.1 showed this `await` blocking for ~20 minutes before the task finally failed (Bug #2).

Key supporting facts verified in the current code:

- `findOpencodeProviderErrorDiagnostic` (`runtime/opencode-log-diagnostics.ts`) is pure bounded file I/O (≤20 log files, ≤10 MB each, scans for `session.id=<id>` + `ERROR` + `service=llm`). It never touches the protocol, so calling it on the timeout path cannot hang.
- `appendOpencodeDiagnostic(message, diagnostic)` appends `\n${diagnostic.summary}` (no-op if absent or already present).
- `AcpProcessHandle.cleanup()` (`SpawnedAcpProcess`) is already bounded: it cancels/aborts the web streams via `Promise.allSettled`, then `killProcess` + SIGKILL after 5 s. It does **not** await any protocol response, so it cannot hang the way `connection.cancel` does.
- `monitorPrompt` is invoked from four callers. Only `runEphemeralWorkflowAgentSession` owns a spawned `acpProcess` (and already runs `finally { acpProcess.cleanup() }`). The three shared callers (`runPromptOnExistingWorkflowAgentSession`, `runResumedWorkflowAgentSession`, `runNewWorkflowAgentSession`) use `context.acpConnection` and have no process handle.
- `cancelAndReturn` is called from three sites inside `monitorPrompt`: the timeout path, the abort path, and the probe-abort path.

## Goals / Non-Goals

**Goals:**

- Make the timeout failure path symmetric with the other failure paths: collect and surface any provider diagnostic and emit a `failed` liveness event with a new `prompt_timeout` failure reason.
- Guarantee `monitorPrompt` returns within a few seconds of any failure, even when the opencode process is hung, by bounding `cancelAndReturn`'s cancel with a hard timeout.
- Let ephemeral sessions be force-cleaned when cancel hangs, while never killing a shared session's process.

**Non-Goals:**

- Do not change the liveness quiet/probe mechanism or its signal sources (`usage_update` is confirmed not a heartbeat; current design is correct).
- Do not change `DEFAULT_TIMEOUT_MS` or the runner↔server config chain.
- Do not pin down the precise root cause of the T-003.1 20-minute hang; the hard-timeout backstop is sufficient.
- Do not change the abort/probe-abort paths' semantics (they remain bare-message cancels; they only gain the bounded-cancel behavior and the `acpProcess` argument).

## Decisions

### Decision 1 — Symmetrize the timeout path (Bug #1)

Replace the one-line timeout return with the same three-step shape used by the other failure paths: query the diagnostic, emit a `failed` liveness event, and return the diagnostic-enriched error. Add `"prompt_timeout"` to the `LivenessFailureReason` union and use it here.

Because `findOpencodeProviderErrorDiagnostic` is bounded file I/O, calling it on the timeout path adds no hang risk. When the log has no provider error, `appendOpencodeDiagnostic` returns the message unchanged, so the no-diagnostic case degrades cleanly to the current message.

### Decision 2 — Keep the cancel on the timeout path and bound it (Bug #2)

The timeout path keeps calling `cancelAndReturn` (now bounded) before returning, rather than dropping the cancel entirely.

- `cancel` is the correct protocol signal to stop the in-flight generation; dropping it would leave opencode running until killed.
- The issue's Scope explicitly threads **three** `cancelAndReturn` call sites with `acpProcess`; keeping the timeout call preserves that count and keeps the abort/probe-abort paths consistent.
- It makes the acceptance criterion ("mock `connection.cancel` hang → `monitorPrompt` returns within timeout + 5 s") exercise the bound through the timeout path rather than passing vacuously.

`cancelAndReturn` gains a leading `acpProcess: AcpProcessHandle | undefined` parameter and a hard timeout:

```ts
const CANCEL_TIMEOUT_MS = 5_000

async function cancelAndReturn(acpProcess, connection, sessionId, error) {
  let cancelled = false
  try {
    await Promise.race([
      connection.cancel({ sessionId }).then(() => { cancelled = true }),
      timeout(CANCEL_TIMEOUT_MS),
    ])
  } catch {}
  if (!cancelled && acpProcess) {
    await acpProcess.cleanup()   // force cleanup so monitorPrompt can return
  }
  return { error }
}
```

`timeout()` and `cleanup()` already exist and are reused. The existing `try/catch {}` swallows any rejection from a cancel that eventually fails after the race resolves.

**Alternative considered:** drop the cancel on the timeout path and rely solely on the ephemeral caller's `finally { acpProcess.cleanup() }`. Rejected — it contradicts the three-call-site scope, gives shared sessions no stop signal, and makes the timeout/cancel-hang acceptance test pass without testing anything.

### Decision 3 — Thread `acpProcess` via `monitorPrompt` options

Add an optional `acpProcess?: AcpProcessHandle` to `monitorPrompt`'s options and forward `options.acpProcess` at the three `cancelAndReturn` call sites. Callers:

- `runEphemeralWorkflowAgentSession` passes its spawned handle.
- The three shared callers omit it (`undefined`).

This encodes the table directly: ephemeral can be force-cleaned on a cancel hang; shared never is, so the shared connection stays reusable.

### Decision 4 — Double-cleanup and dangling promise safety (ephemeral)

On the ephemeral timeout path, if cancel hangs, `cancelAndReturn` calls `acpProcess.cleanup()`, then the caller's `finally { acpProcess.cleanup() }` runs again. This is safe: `cleanup()` cancels/aborts web streams (no-ops when already closed) and guards the kill with `if (!this.exited)`. The background `promptPromise` may reject when the process dies, but `monitorPrompt` already attaches a no-op rejection handler (`promptPromise.then(usage, () => {})`), so there is no unhandled rejection and `monitorPrompt` returns without awaiting it.

## Risks / Trade-offs

- [Risk: 5 s hard timeout is ungraceful for slow-but-healthy cancels] -> Mitigation: 5 s only bounds the *cancel RPC*, not the prompt; a healthy cancel returns well under 5 s. The value matches the existing SIGKILL grace in `cleanup()`, keeping the two bounds consistent.
- [Risk: ephemeral `cleanup()` on cancel-hang races the caller's `finally` cleanup] -> Mitigation: `cleanup()` is idempotent by construction (Decision 4). No double-kill.
- [Risk: `"prompt_timeout"` breaks a downstream exhaustive switch] -> Mitigation: additive enum member. Server deserialization (`dotnet test`) and the Web `failureCategory` switch are scanned; `prompt_timeout` falls through to default rendering. Covered by acceptance checks.
- [Trade-off: shared sessions get no process kill on cancel-hang] -> Intentional. Killing a shared process would poison the connection for later tasks. The hung cancel promise is abandoned (swallowed by `catch`); the shared session is already being declared failed.

## Migration Plan

This change is additive and deploys in a single runner+server release with no data migration:

1. Ship the runner change (`acp-agent.ts` + tests).
2. Verify `dotnet test Mohist.sln` — the new enum member is additive to server-side deserialization.
3. Rollback is a pure revert of the runner change; no schema or persisted-state change to undo. Existing in-flight runs are unaffected because the new failure reason is only produced by the new code.

## Open Questions

- None blocking. The 5 s `CANCEL_TIMEOUT_MS` is taken from the issue's design direction; if observability later shows healthy cancels approaching that bound, it can be tuned independently (it is a local constant, not config-synced — intentionally out of scope).
