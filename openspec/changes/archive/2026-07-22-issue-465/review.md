# Review

## Findings

No blocking findings.

The implementation preserves raw `with` and `expect` declarations in Server dispatches, renders cloned inputs at the Runner execution boundary, keeps deferred fields intact, and preserves raw declarations when constructing recovery self-retries. The attempt variable snapshot remains the rendering source, so subsequent retries can use updated stage variables without changing an already dispatched attempt. The synchronized design artifacts describe the same boundary and recovery invariant.

Focused verification passed:

- `npm run typecheck -w packages/runner`
- Runner raw-input, recovery, and cross-boundary tests: 19 tests passed
- Affected Server spec filters: 39 tests passed
- `git diff --check origin/master...HEAD`

<promise>PASS</promise>
