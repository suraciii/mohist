# Review: Issue 618

Review round: re-review of the current product change against the live issue acceptance criteria and the issue-618 plan/spec artifacts. The previous review was FAIL with three must-fix findings. The current revision includes the dispositions for all three; I checked those fixes and the regressions they could introduce.

## Previous Finding Dispositions

1. **Unauthenticated Manager broker: fixed properly.** The broker now rejects a socket unless Linux peer inspection resolves the connected process to the exact generated launcher path (`packages/runner/src/runtime/manager-execution-boundary.ts:337-355`; `packages/runner/src/runtime/manager-launcher-auth.ts:21-35`). A direct non-launcher socket client cannot submit either a valid management request or a valid reply request, and no CLI child is spawned. The same boundary still applies the capability catalog, kind match, frozen working directory, per-kind budget, and output masking (`manager-execution-boundary.ts:365-396`). The regression test covers both direct valid requests and confirms zero invocations (`manager-execution-boundary.test.ts:97-119`). This closes the previous violation of Acceptance Criterion 4 and the runtime-only credential requirement.
2. **Token-bearing child cleanup: fixed properly and not regressed.** Disposal still shuts down the isolated runtime, terminates tracked children with bounded SIGTERM/SIGKILL escalation, destroys active sockets, closes the broker, and removes the execution directory only afterward (`manager-execution-boundary.ts:224-264`). The bounded-disposal test remains in the focused suite.
3. **Runner-loss Manager recovery: fixed properly and not regressed.** Server-side Runner-loss handling still delivers the pending initial terminal and creates the single Manager recovery transition (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:132-143`), while the existing idempotency guard prevents a second recovery transition (`:29-32`). The full Server Spec suite, including the Manager Runner-loss recovery cases, passes.

## Dimension Checks

- **Issue acceptance criteria: checked, no issue.** The prior criteria 1-3 and 5-8 remain satisfied. Criterion 4 is now satisfied: valid direct broker requests are refused, credentials are only installed in the spawned CLI child (`manager-execution-boundary.ts:386-396`), and the server continues to enforce lease scope, origin, current authorization, and reply ownership.
- **Coverage: checked, no issue.** The repaired behavior has an integration-level broker test for direct management and reply attempts, launcher admission, capability confinement, kind mismatch, request budgets, frozen cwd, masking, and disposal. Existing Server, CLI, and adapter coverage remains present for natural-language Manager turns, reply ownership, recovery, allowlist enforcement, liveness, and ordinary Slack regressions.
- **Correctness: checked, no issue.** Manager work is dispatched only to runners advertising the complete required capability set (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:201-203,263-266`), and the Runner advertises and accepts that boundary only on Linux (`packages/runner/src/runtime/registration-state.ts:25-38`; `packages/runner/src/runtime/host-helpers.ts:36-41`). The latest platform gate prevents the prior non-Linux path from claiming support for a broker that cannot authenticate peers.
- **Consistency with surrounding codebase and conventions: checked, no issue.** The repair is localized to the Runner process boundary and registration/dispatch capability gates. It preserves the existing Server grant transport, ordinary non-Manager dispatch, shared CLI catalog, and current authorization paths.
- **Tests: checked, no issue.** Focused Runner boundary tests pass 7/7. Full Runner Vitest passes 156 files and 1,688 tests; Runner typecheck and build pass. Server Unit passes 3,121 tests, Server Spec passes 3,711 tests, CLI passes 1,950 tests, and format/file-size checks pass.

## Observations

- Manager execution is deliberately Linux-only in the current implementation. Non-Linux runners do not advertise Manager capabilities and the broker refuses to start outside Linux (`manager-execution-boundary.ts:304-306`). The design artifact describes a future Windows named-pipe adapter, but the live issue acceptance criteria do not require cross-platform Manager execution, so this does not affect the verdict.
- Peer authentication fails closed when Linux `/proc` or the `ss` socket-inspection utility is unavailable. That is appropriate for the credential boundary; deployment packaging must provide those Linux facilities for Manager work to be usable.

**Verdict: PASS**
<promise>PASS</promise>
