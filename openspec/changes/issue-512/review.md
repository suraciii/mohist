# Review: Issue 512

## Findings

### P1: The launch route can report acceptance before all four launch facts are durable

`AgentLaunchCoordinatorGrain.AdvanceAsync` catches every participant exception and only logs it (`packages/server/src/Mohist.Server/Agent/Grains/AgentLaunchCoordinatorGrain.cs:186-208`). `LaunchAsync` then unconditionally returns the plan's Job, Session, Input, and Turn IDs after `AdvanceAsync` returns (`lines 126-141`), and the HTTP route converts that result to `201` (`packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:139-163`).

For example, if `PrepareManualLaunchAsync` succeeds but `EnsureInitialLaunchAsync` throws, the nested `AdvanceAsync` call swallows the failure, leaves the persisted plan fenced at `EnsureInitialLaunch`, and control returns all the way to the route. The caller receives `201` although the Session may not yet exist and the accepted Input and queued Turn have not been stored. This violates D2/D3's requirement that `201` is returned only after the Job, Session, Input, and Turn are durable. Keep the reminder-based retry for recovery, but make the synchronous launch path expose incomplete setup as non-accepted (or propagate the participant failure) until `Plan.Completed` is true. Add failure-injection specs for each coordinator fence that assert no `201` before the corresponding durable participant write completes.

## Verification

- Previous full validation recorded in `progress.txt`: `npm test`, Runner typecheck/tests, and Web typecheck/tests/FSD check passed.

<promise>FAIL</promise>
