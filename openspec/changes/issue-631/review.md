# Review

## Verdict

PASS. The implementation keeps provider-limit handling inside the Runner and changes only recovery eligibility after the existing bounded runtime policy has judged the provider failure non-recoverable.

## Findings

- OpenCode's existing `provider-quota-exhausted` diagnostic is mapped to the internal `provider-quota-exhausted` Action error code before the shared recovery boundary.
- Pi's existing provider policy now carries the original provider retry message and the same diagnostic marker into the Action result.
- `tryRecovery` returns the original failed result for that marker before selecting a handler or creating `retrySelf` work. Ordinary failure codes retain the existing handler and budget behavior.
- No Server, CLI, Web, persistence, terminal-state, cross-run admission, or Retry-After contract was added.

## Verification

- `npm run test:ci -w packages/runner`: 169 files, 1852 tests passed.
- Runner typecheck, test-boundary check, format check, file-size ratchet, and `git diff --check` passed.
- Focused runtime/recovery tests passed 112/112, including quota message preservation and ordinary recovery regression.

No must-fix findings remain.

<promise>PASS</promise>
