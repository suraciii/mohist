# Self Review: Issue 461

## Findings

### 1. High: production follow-up handling resolves the runtime before it exists

The plan requires follow-up input to be durably enqueued and then invoked exactly once through the current OpenCode runtime (`specs/agent-session-runtime-event-delivery/spec.md:100-110`, `tasks.json:26-28`). It does not address a current composition defect on that path.

`RunnerHost` constructs `RunnerSignalRClient` with a runtime accessor while `openCodeRuntime` is still null (`packages/runner/src/runtime/host.ts:156-168`); the runtime is created later in `initializeSharedConnection` (`packages/runner/src/runtime/host.ts:325-337`). `RunnerSignalRClient` registers handlers in its constructor and immediately calls `resolveOpenCodeRuntime()`, passing the resulting value into the follow-up and cancel handlers (`packages/runner/src/server/runner-signalr.ts:90-105`, `packages/runner/src/server/runner-signalr.ts:139-161`). The accessor is therefore resolved once to null rather than at command invocation time.

Without an explicit correction, production follow-ups remain `unavailable` before durable enqueue or runtime invocation, so the issue's follow-up-input guarantee cannot be delivered. The plan must require follow-up/cancel handlers to resolve the runtime at invocation time (or receive an updated live handle) and add a real `RunnerSignalRClient` regression test where the client is constructed before the runtime becomes ready and a later follow-up uses the initialized runtime. A host test that only inspects an accessor through a mocked client is insufficient.

## Review Summary

- The proposal and specs cover upload retry, restart recovery, non-blocking Server delivery, and the issue's Server/cross-runner non-goals.
- Managed producer-sequence ordering, AgentJob scope, acknowledgement policies, stale-binding behavior, local persistence failure handling, autonomous health recovery, migration, atomic switchover, and runner `test:ci` verification are internally consistent.
- The remaining blocker is the live runtime composition required for the follow-up feature to function in production.

## Verdict

The plan is not ready to build.

<promise>FAIL</promise>
