# Review

## Findings

### 1. High: Persisted Follow-up claims can acknowledge an input that was never submitted

`packages/runner/src/runtime/followup-operation-journal.ts:42-49` persists only a set membership claim and has no state distinguishing `claimed`, `submitted`, and `completed`. `packages/runner/src/server/followup-handler.ts:167-174` treats every later `claim()` returning `false` as a successful duplicate and returns `{ accepted: true }` without enqueueing or invoking the runtime. If the runner process crashes after the claim is persisted but before `callFollowup` is invoked, or if `callFollowup` throws synchronously at `:216-221`, a retry with the same operation ID is silently acknowledged and the prompt is lost. The operation journal must preserve the indeterminate state and make retries fail explicitly, or otherwise record a durable submission outcome; only a known completed/submitted operation may be acknowledged as an already-delivered duplicate. Add a regression test covering a claim persisted before runtime invocation followed by a new handler/process instance.

The previous review findings are otherwise addressed: Follow-up duplicates are guarded, confirmed runtime failures emit `idle`, and Runner disconnect cleanup is conditional on the connection ID.

## Verification

The current focused verification passes: runner typecheck, the Follow-up handler tests (24 tests), the RunnerHub specs (3 tests), the full runner suite (1,380 tests), the full Web suite (5,127 tests with one skip), and the full .NET solution suite (5,685 tests across the reported projects).

<promise>FAIL</promise>
