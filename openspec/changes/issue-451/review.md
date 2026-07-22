# Review

## Findings

### P1: Missing-session handling is bypassed while the cached Pi session is streaming

`packages/runner/src/runtime/pi/runtime.ts:461-467` returns a cached session immediately when `cached.isStreaming` is true, without calling `validateSessionFile`. Thus Follow-up and Cancel during an active turn can use a stale in-memory handle after the bound Pi session file has been deleted or corrupted and report success instead of returning `missing-session` with a Reset hint, contrary to the acceptance criterion and channel spec, which apply to Follow-up/Cancel regardless of whether the turn is active. The new test in `packages/runner/tests/pi-runtime-session-validation.test.ts:37-49` explicitly locks in this bypass by asserting the validator is not called while streaming. Validation must remain non-owning but must also cover the active-session path, with a regression test for a missing/corrupt file during a busy turn.

<promise>FAIL</promise>
