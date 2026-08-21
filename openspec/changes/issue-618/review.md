# Review: Issue 618

Review round: re-review of the current product change against the live issue acceptance criteria and the issue-618 plan/spec artifacts. The previous review was FAIL with three must-fix findings. I verified each disposition and checked for regressions from the current two-file diff.

## Must-Fix Findings

### 1. The Manager broker is still callable by a generic model shell

`ManagerExecutionBoundary.environment()` puts `MOHIST_MANAGER_BROKER` into the environment returned to the runtime (`packages/runner/src/runtime/manager-execution-boundary.ts:140-150`). The same environment is passed to generic Bash commands (`packages/runner/src/runtime/manager-execution-boundary.ts:153-167`), so any process in the Manager runtime can open that socket directly.

The broker does not authenticate that a request came from the generated `mo` launcher. `handleConnection` parses any socket client's JSON and passes it to `admitRequest` (`packages/runner/src/runtime/manager-execution-boundary.ts:319-346`). `admitRequest` validates only the declared kind, argument shape, catalog capability, liveness, and request budget (`packages/runner/src/runtime/manager-execution-boundary.ts:349-374`). A generic shell can therefore submit a valid request such as `{ kind: "management", args: ["slack", "status"] }` or `{ kind: "reply", args: ["slack", "message", "send", ...] }` without using the launcher. The broker then spawns the real `mo` child with the corresponding plaintext lease (`packages/runner/src/runtime/manager-execution-boundary.ts:377-389`). The bearer is not returned to the shell, but the shell can still cause the credential-bearing privileged operation and receive its result.

The catalog gate limits which operations can be proxied, but it does not establish the required scoped CLI-child boundary. This violates issue Acceptance Criterion 4 and the `manager-execution-credentials` requirement that the credential be available only to the scoped Manager CLI child, with generic-shell isolation. It also conflicts with the plan's explicit broker contract that requests without the launcher boundary are rejected. The boundary needs an unforgeable launcher/process authorization mechanism or a design in which a generic shell cannot submit requests to the broker. Add a regression test that sends a valid allowlisted management and reply request directly from a non-launcher socket client and verifies that neither operation runs.

The current test only exercises the old malformed/no-argument request (`packages/runner/src/runtime/manager-execution-boundary.test.ts:87-92`); it does not test this valid direct-proxy attack. The focused boundary suite passes 7/7, but that result does not close this finding.

## Previous Finding Dispositions

1. **Unauthenticated Manager command proxy: not fixed.** The old raw-bearer response shape was removed and catalog confinement was added, but the broker remains an unauthenticated command proxy as described above. The prior must-fix finding remains open.
2. **Token-bearing child cleanup: fixed properly.** The boundary tracks spawned CLI children, terminates them with bounded SIGTERM/SIGKILL cleanup, destroys active broker sockets, and removes the execution directory only afterward (`manager-execution-boundary.ts:224-260`). The focused boundary test covering a child that ignores SIGTERM passed.
3. **Runner-loss Manager recovery: fixed properly.** Server-side Runner loss now calls `EnsureManagerRecoveryAsync` from `MarkUnknownAsync`; it records the inspection-only follow-up and then calls `MarkInitialTurnTerminalAsync`, whose existing scheduler call dispatches the queued follow-up (`AgentJobGrain.Recovery.cs:132-143`; `AgentJobGrain.Recovery.cs:42-73`; `AgentSessionGrain.cs:3432-3463`). The Manager Runner-loss recovery specs pass within the full Server Spec assembly, and the original uncertain dispatch remains suppressed.

## Dimension Checks

- **Issue acceptance criteria: FAIL.** Criteria 1-3 and 5-8 have no new must-fix issue found in this round. Criterion 4 remains violated by the generic-shell broker bypass.
- **Coverage: FAIL.** The implementation has tests for catalog confinement, credential masking, request budgets, and disposal, but no test for a valid request submitted directly by a generic runtime process. That missing case is the attack that keeps Finding 1 open.
- **Correctness: FAIL.** A Manager model shell can invoke the broker without going through the generated CLI launcher, so execution-level credential confinement is not actually enforced.
- **Consistency with surrounding codebase and conventions: checked, no additional issue.** The remaining mismatch is the must-fix boundary failure above.
- **Tests: FAIL for the must-fix behavior.** `npm run test:run -w packages/runner -- src/runtime/manager-execution-boundary.test.ts` passed 7/7; `npm run typecheck -w packages/runner` passed; and the Server Spec run passed 3,711/3,711. The requested Server filter was ignored by Microsoft Testing Platform, so the full assembly ran. None of these tests exercises a valid non-launcher broker request.

## Observations

- `manager-capability-surface.ts` is a manually maintained TypeScript mirror of the C# catalog. Its `--manager=true` handling is case-sensitive while the CLI's corresponding mode flag is case-insensitive. This is a small compatibility/drift risk, not a must-fix issue for the stated acceptance criteria.
- The current change also admits bare/help requests as management-kind child processes (`manager-capability-surface.ts:91-101`); these mirror CLI help behavior and do not themselves create a new acceptance failure.

**Verdict: FAIL**
<promise>FAIL</promise>