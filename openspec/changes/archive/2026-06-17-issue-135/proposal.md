## Why

When an LLM provider silently hangs mid-stream, the runner's `monitorPrompt` hits its prompt timeout but reports only a bare "Timed out after 1800s" — unlike every other failure path, the timeout path never queries the opencode log, so the real root cause (e.g. `Token Plan usage limit reached`) is masked. Worse, the post-timeout cleanup in `cancelAndReturn` has no hard timeout of its own: it `await`s `connection.cancel` unboundedly, and when the opencode process is hung that await blocked for ~20 minutes in production (issue #126 / T-003.1), leaving the task in a "stuck" state with no feedback. Both gaps must be closed so failures are diagnosable and cleanup is bounded.

## What Changes

- Symmetrize the prompt-timeout failure path in `monitorPrompt` with the other four failure paths: query the opencode log via `findOpencodeProviderErrorDiagnostic`, emit a `failed` liveness status event with the diagnostic, and return the diagnostic appended to the error message — instead of returning a bare timeout string.
- Add `"prompt_timeout"` as a new member of the `LivenessFailureReason` union and use it as the failure reason on the timeout path.
- Bound the post-failure cleanup in `cancelAndReturn` with a 5s hard timeout (`Promise.race` against `connection.cancel`).
- On cleanup timeout in the **ephemeral** session path, force cleanup by calling `acpProcess.cleanup()` (killProcess + SIGKILL after 5s) so `monitorPrompt` can return.
- On cleanup timeout in the **shared** session path, do **not** kill the process — let the cancelled promise settle (already swallowed) so the shared connection stays reusable by later tasks.
- Adjust the three `cancelAndReturn` call sites to pass the `acpProcess` handle on the ephemeral path and `undefined` on the shared paths.
- Add unit tests covering: diagnostic surfaced when the opencode log has a provider error; no diagnostic when the log is empty; `emitLivenessStatusEvent` called with `failureReason="prompt_timeout"`; ephemeral cleanup-timeout triggers `acpProcess.cleanup()`; normal cancel returns without cleanup; shared path never calls `cleanup()`/killProcess.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `agent-runtime` — ACP session execution must, on prompt-level timeout, collect and surface provider diagnostics and report a `prompt_timeout` failure reason like the other failure paths; and post-failure cleanup must be bounded by a hard timeout, force-cleaning ephemeral sessions while never killing shared sessions.

## Impact

- **Runner / agent runtime**: `packages/runner/src/actions/acp-agent.ts` — `monitorPrompt` timeout branch (the `timeoutRemaining <= 0` path), the `LivenessFailureReason` union, `cancelAndReturn` signature + body, and the three call sites (`runEphemeralWorkflowAgentSession` passes the process handle; the shared `runPromptOnExistingWorkflowAgentSession` / `runResumedWorkflowAgentSession` / `runNewWorkflowAgentSession` paths pass `undefined`).
- **Failure-reason contract**: `LivenessFailureReason` gains an additive member. Downstream consumers (`.NET` server deserialization, Web `failureCategory` switch, CLI status rendering) only need to fall back to default rendering for `"prompt_timeout"`; no existing branch should break.
- **Ephemeral cleanup**: when a cancel hangs, `acpProcess.cleanup()` kills the opencode process; the resulting stdout stream close must reject `promptOutcome` (via `exitFailure`) so `monitorPrompt` still resolves rather than hanging forever.
- **Tests**: `packages/runner/tests/acp-agent.spec.ts` gains the six unit cases listed above; `npm --prefix packages/runner test` and `dotnet test Mohist.sln` must stay green.
- **Non-goals**: liveness quiet/probe thresholds, `usage_update` signal source, `DEFAULT_TIMEOUT_MS`, and the config-sync chain are intentionally untouched.
