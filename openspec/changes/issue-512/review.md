# Review: Issue 512

## Findings

### P1: Submit-fence recovery is not tested for exactly-one dispatch

`Launch_ParticipantFailureAtFence_Returns503AndRecoversWithSameKey` exercises `SubmitJob`, but it never registers a Runner or observes `IAgentJobDispatchObserver` (`packages/server/tests/Mohist.Server.SpecTests/Specs/Agent/Api/AgentSessionLaunchIdempotencySpecs.cs:135-166`). The assertions prove the route returns `503`, recovers the four identities, and leaves the Session's initial children stable, but they cannot detect whether the first `SubmitPreparedLaunchAsync` dispatches work and the recovery call dispatches it again after the post-submit acknowledgement-loss probe throws.

This is the highest-risk recovery fence: the probe now correctly throws after `SubmitPreparedLaunchAsync` (`packages/server/src/Mohist.Server/Agent/Grains/AgentLaunchCoordinatorGrain.cs:349-351`), so the retry necessarily invokes that method a second time. T-001 explicitly requires all recovery fences to verify exactly-one Job dispatch using the fake dispatch observer. Register a Runner in the SubmitJob case, force the first post-submit acknowledgement loss, recover with the same key, and assert exactly one dispatched work item/observer event for the returned Job before cleaning up the Runner.

## Verification

- Recorded verification: focused `AgentSessionLaunchIdempotencySpecs` 8/8 pass. The latest `npm test` passed with Workflow Definition 175, Server Unit 1533, Server Architecture 35, Server SpecTests 3292, CLI 1435, Web 5153 (1 skipped), and Runner 1438 tests.

<promise>FAIL</promise>
