# Review Findings

## F-1: BLOCKING - warning injection uses the wrong SDK v2 request shape

`packages/runner/src/runtime/opencode/turn.ts:508` calls
`client.session.promptAsync()` with the legacy Hey API envelope:

```ts
{ path: { id }, query: { directory }, body: { parts } }
```

The real-OpenCode smoke record at
`openspec/changes/issue-439/deadline-warning-smoke.json:20-30` establishes that
the pinned SDK v2 call requires the flat shape
`{ sessionID, directory, parts }`. The smoke itself calls out this exact
mismatch at lines 131-135. Consequently the fake clients accept the warning,
transcript acceptance criteria are not met. Align the production call with the
SDK v2 parameter shape and update the fake-client assertions so this cannot
regress.

## F-2: BLOCKING - deadline and warning timing begin after session resolution

`runTurn` creates the timeout signal at
`packages/runner/src/runtime/opencode/turn.ts:116-118`, but the warning timer
is not scheduled until `executePrompt` at lines 307-308, after
`resolvePhysicalSession` completes. The deadline timer can therefore fire
while `session.create` or `session.get` is still pending; that path returns
`interrupted` without calling `client.session.abort()` because no physical
session ID has been resolved. When session resolution is merely slow rather
than stalled, the warning timer is also delayed by that setup time, so it no
longer fires at deadline-minus-five-minutes from turn start.

This violates the declared per-turn deadline contract and the acceptance
criterion that an in-flight turn is terminated via `client.session.abort()` at
the deadline. Schedule against a single turn-start deadline and make the
session-resolution phase cancellation-aware enough that a deadline cannot
silently return before the required abort behavior for an already-created
session. Add fake-clock coverage with a deliberately delayed session create/get
to pin the timing boundary.

## Verification

`npm run typecheck -w packages/runner` passed.

`npm run test:run -w packages/runner` passed: 95 files, 1167 tests.

<promise>FAIL</promise>
