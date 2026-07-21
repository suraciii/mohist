## Findings

No merge-blocking findings. The runner now treats the workflow route's empty accepted-event response as a rejected `session.input`, suppressing later activity and terminal uploads for that turn while preserving the OpenCode Action result.

Verification: `npm run typecheck -w packages/runner` and `npm test -w packages/runner` (1191 tests) passed.

<promise>PASS</promise>
