# Review Findings

## P1. Runner restart does not run binding reconciliation

`packages/runner/src/runtime/host.ts:266-271` runs the existing workflow convergence after `connectRunner`, but never calls `runBindingConvergenceOnce`. The new binding pass is only invoked from `onDispatchReconnected` at lines 367-378, which SignalR raises for an automatic transport reconnect, not for the initial `connection.start()` of a newly restarted Runner process. Therefore the #489 case, where the Runner process exits and starts a fresh connection, leaves persisted `AgentSession` activity as `unknown` and never probes or settles the still-existing OpenCode Session. This fails the automatic Runner restart recovery criterion. Invoke the binding convergence after the initial connection is established as well, with the same runtime/outbox readiness guarantees as the reconnect path.

## P1. OpenCode corrupt probe responses authorize replacement

`packages/runner/src/runtime/opencode/runtime.ts:138-141` maps a missing response body or an ID mismatch from `session.get` to `normalizeMissingSession()`. Those are malformed or unclassifiable responses, not confirmed evidence that the physical Session is absent. `binding-convergence.ts:68-77` treats only the `missing-session` kind as authorization to create and bind an empty replacement. Consequently, a corrupt SDK response can replace a still-existing Session and lose its context, violating the non-recovery requirement for corrupt responses. Return an explicit non-recovery error for malformed/mismatched data and add a test proving no candidate is created.

## P1. Pi probe maps every open failure to confirmed missing

`packages/runner/src/runtime/pi/runtime.ts:91-100` catches every failure from `openSession` and returns `failure("missing-session", ...)`, including transport errors, permission/filesystem failures, corrupt session data, and runtime unavailability. The reconnect path then treats that result as confirmed missing in `binding-convergence.ts:68-77`, creates a new Session, and asks the Server to rebind. This violates the acceptance criteria that transient, transport, unavailable, and corrupt results preserve the binding and remain `unknown`. Classify only a verified missing-file condition as `missing-session`; preserve all other failures as non-recovery results and cover those cases with tests.

## P1. Concurrent task recovery and reconnect recovery can create multiple candidates

`packages/runner/src/runtime/binding-convergence.ts:70-77` creates the replacement before the server-side binding CAS. The `runOnce` mutex only serializes binding-convergence passes; it does not coordinate with the existing `resolveOrRecoverBinding` path used by task/retry/follow-up execution. If both paths probe the same old binding as missing before either replacement request commits, both call `createEmptySession`, and the CAS only rejects one rebind after its candidate has already been created. The rejected candidate remains an empty physical Session, so repeated reconnect/retry attempts can leave multiple candidates, contrary to the one-candidate acceptance criterion. Coordinate candidate creation with the authoritative binding operation or otherwise make candidate creation idempotent and clean up losing candidates.

Verification: `npm test`, `npm run typecheck -w packages/runner`, and `npm test -w packages/runner` passed.

<promise>FAIL</promise>
