# Review Findings

## P1. Status-probe 404 is treated as confirmed session loss

`packages/runner/src/runtime/opencode/runtime.ts:133-168` wraps both the typed `session.get` existence check and the subsequent `session.status` active-turn snapshot in one `try/catch`. The catch maps any error whose status is `404` to `normalizeMissingSession()`, even when the 404 came from `session.status` after `session.get` has already returned the requested Session. `packages/runner/src/runtime/binding-convergence.ts:64-77` treats `missing-session` as authoritative and creates a new empty Session before asking the Server to rebind.

A missing or unavailable status endpoint, or a status response that returns 404 for a transient reason, is not confirmation that the physical Session is absent. This can replace a still-queryable OpenCode context and violates the acceptance criterion that timeout, transport, unavailable, and corrupt/unclassifiable results preserve the binding. Restrict `missing-session` classification to a 404 from `session.get`; classify failures from `session.status` as non-recovery errors, and add a regression test proving a status-probe 404 does not create or bind a candidate.

## Resolution

`OpenCodeRuntime.resolveSession` now runs `session.get` (existence) and `session.status` (active-turn snapshot) in **separate** `try/catch` blocks. Only a `404` from `session.get` resolves to `missing-session`; any failure from `session.status` — including a `404`, transport failure, or corrupt response — resolves to `turn-failed`. Because `binding-recovery.ts` only treats `missing-session` as authoritative-missing recovery (every other kind returns a non-recovery failure that preserves the binding), a status-probe failure can no longer authorize replacement of a still-queryable physical Session.

Regression coverage added in `packages/runner/tests/opencode-runtime.spec.ts` (`OpenCodeRuntime.resolveSession`):

- a `session.status` `404` after a successful `session.get` resolves to `turn-failed`, never `missing-session` (the direct regression);
- a confirmed-missing `session.get` `404` still resolves to `missing-session` and never calls `session.status`;
- a non-404 `session.get` failure resolves to `turn-failed`.

The convergence layer (`packages/runner/tests/binding-convergence.spec.ts`) already asserts that `turn-failed` (along with `deadline-exceeded`, `unavailable-runtime`, `incompatible-runtime`) preserves the binding without creating a candidate or invoking `reconcileMissingAgentSession`. Runner typecheck passes; 154 opencode-family tests pass.

<promise>PASS</promise>
