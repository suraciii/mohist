# Review: Issue 512

## Findings

### P1: Fence recovery changes the Prepare command identity instead of resuming it

When a persisted `PrepareJob` fence is retried, `ResumePendingAsync` calls `BeginPrepareAsync` (`packages/server/src/Mohist.Server/Agent/Grains/AgentLaunchCoordinatorGrain.cs:262-270`). `BeginPrepareAsync` always generates a new `commandId` and writes it into `Pending` (`lines 218-229`), overwriting the identifier of the durable command that failed. This differs from the Ensure and Submit paths, which reuse `plan.Pending.CommandId` (`lines 282-293` and `336-347`).

D2 requires activation/retry to resume the same prepared command, with the persisted command fence retained until the participant explicitly reports applied or already-applied. Reusing generated Job/Session/Input/Turn IDs happens to make the current participants idempotent, but it does not preserve the actual fence contract and will break correlation or participant-level command deduplication as soon as either uses `CommandId`. Retain the existing pending Prepare `CommandId` on recovery, and add an assertion that the durable fence identity remains unchanged across a failed Prepare retry.

### P1: The failure probe fires before the participant call and cannot cover an applied-but-unacknowledged command

`IAgentLaunchParticipantProbe` is invoked immediately before each participant method (`packages/server/src/Mohist.Server/Agent/Grains/AgentLaunchCoordinatorGrain.cs:231-233`, `313-314`, and `349-351`). The test probe throws there (`packages/server/tests/Mohist.Server.SpecTests/Support/AgentLaunchParticipantProbe.cs:54-64`), so `Launch_ParticipantFailureAtFence_Returns503AndRecoversWithSameKey` only proves recovery when no participant received the command.

The D2 recovery case also includes a participant that durably applied the command but whose acknowledgement was lost. That is the case requiring the same command fence and an `AlreadyApplied` response; it is not exercised by the new probe or route-level specs. Add a test seam that fails after each participant has applied its idempotent command but before the coordinator advances its fence, then verify same-key recovery issues no duplicate Job dispatch, Input, or Turn.

## Verification

- Focused: `AgentSessionLaunchIdempotencySpecs` 8/8 pass; all launch/session/job spec classes 449/449 pass.
- `npm test`: Workflow Definition 175, Server Unit 1533, CLI 1435, Arch 35, Server SpecTests 3291/3292 pass. The single SpecTests failure is the pre-existing, unrelated `AgentSessionEventDiscardObservabilitySpecs.MixedBatch_DiscardsRetiredTypesAndProcessesSupportedEvents` flake (documented in `progress.txt`; uses a separate `AgentSessionGrainFixture`, passes in isolation, fails intermittently only under full-suite load). Runner/Web typechecks pass.

<promise>FAIL</promise>
