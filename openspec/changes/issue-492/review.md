# Review Findings — Issue #492

## Scope

This review covers commit `47464b2bd` which fixes a P1 finding from the prior
review: `OpenCodeRuntime.resolveSession` wrapped `session.get` (existence) and
`session.status` (active-turn snapshot) in a single `try/catch`, so a 404 or
transient failure from `session.status` was misclassified as `missing-session`
— authorizing replacement of a still-queryable binding. The fix splits the two
calls into separate `try/catch` blocks and adds regression coverage.

The only changed product files are:

- `packages/runner/src/runtime/opencode/runtime.ts` — `resolveSession` method
- `packages/runner/tests/opencode-runtime.spec.ts` — `resolveSession` tests

## Verification

### Fix correctness

The `resolveSession` method now has two independent `try/catch` blocks:

1. **`session.get` block** (`runtime.ts:134-152`): only a 404 from `session.get`
   resolves to `missing-session`. A malformed or mismatched response resolves to
   `turn-failed`. Any other error resolves to `turn-failed`. This is the sole
   gate for authoritative missing-session classification.

2. **`session.status` block** (`runtime.ts:153-176`): all failures (404,
   transport, corrupt response, missing status map) resolve to `turn-failed` with
   the message "Failed to read Runtime Session active-turn status". The
   `binding-recovery.ts` module only treats `missing-session` as authoritative
   for recovery; `turn-failed` is a non-recovery error that preserves the
   binding.

The `sessionData` variable is declared before the first `try`, assigned within
it, and used in the second `try`. TypeScript's definite-assignment analysis is
satisfied because the first `catch` block always returns — which TypeScript
correctly tracks (typecheck passes, 0 errors).

The `activeTurn` computation (`status !== undefined && status.type !== "idle"`)
is correct: a session absent from the status map is treated as idle, and a
session with a non-`"idle"` status type is treated as active.

### Typed SDK contract

The `session.get` call uses `{ sessionID, directory }` and the `session.status`
call uses `{ directory }` — both are the typed OpenCode SDK request contract
already used by `turn.ts`. No `as never` casts remain in `resolveSession`. The
`followup` and `cancel` methods already used typed calls (verified at
`runtime.ts:318-321`, `:338-344`, `:394-397`).

### Test coverage

Six new test cases in `opencode-runtime.spec.ts`:

| Test | What it proves |
|------|---------------|
| preserve idle binding + activeTurn snapshot | `session.get` succeeds, `session.status` returns idle → `ok=true, activeTurn=false` |
| activeTurn true for streaming | `session.status` returns `"streaming"` → `activeTurn=true` |
| session.get 404 → missing-session | `session.get` throws 404 → `missing-session`, `session.status` never called |
| status-probe 404 → turn-failed (not missing) | `session.get` succeeds, `session.status` throws 404 → `turn-failed`, NOT `missing-session` |
| non-404 session.get failure → turn-failed | `session.get` throws 500 → `turn-failed` |
| unavailable runtime | runtime not started → `unavailable-runtime` |
| null sessionId → missing-session | no `runtimeSessionId` bound → `missing-session` |

The direct regression (status-probe 404 after successful session.get → not
missing-session) is covered by the fourth test. The fifth test confirms the
non-404 path is also not misclassified.

### Alignment with acceptance criteria

- **AC #3** (never classified as missing): a status-probe failure after a
  successful `session.get` is now `turn-failed`, not `missing-session`. The
  binding is preserved. ✓
- **AC #5** (non-recovery conditions preserve binding): status-probe failures
  (404, transport, corrupt) are non-recovery errors that preserve the binding. ✓
- **AC #6** (old-binding facts cannot change current state): the existing CAS
  and binding-guarded channels handle this — no new code needed per D7. ✓

The remaining acceptance criteria (AC #1, #2, #4, #7, #8) are addressed by
T-002 and T-003, which are not part of this change.

### Test results

```
npm run typecheck -w packages/runner  →  passes (0 errors)
npm test -w packages/runner           →  120 files, 1424 tests passed
```

## Findings

No problems found. The fix is minimal, correct, and well-tested.

<promise>PASS</promise>