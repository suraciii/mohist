# Review - Issue 617

## Must-fix findings

### MF-1 - Mixed-source follow-up batching can permanently block a Session

**Criterion violated:** T-002 acceptance criterion 7 (non-Slack follow-ups must retain their existing non-Slack execution behavior and envelope), plus the issue non-goal that Web, CLI, and Workflow execution must remain unchanged. The source discriminator boundary also requires one valid source/context pair per dispatch.

**Where:** `packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:1257-1277`, `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1053-1056`, and `packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:46-48`.

**Problem:** `ChooseFollowupTurnForAssignment` joins an incoming follow-up to any unclaimed queued turn without checking its execution source. A valid scenario is a Slack Session whose queued Slack follow-up is waiting because its Runner is unavailable, followed by a Web or CLI follow-up for the same Session. Both inputs are then placed in one turn. `BeginNextFollowupDispatchAsync` selects the first input's source and throws when the other input has a different source. The call occurs before the dispatcher's `try` block, so the exception is not converted into a release or an unavailable result; the queued turn remains undispatched and repeats the same failure on retry.

This means the non-Slack follow-up is neither delivered with its unchanged non-Slack envelope nor given a separate turn, and the Slack work is also stuck. Make queued-turn assignment source-aware, or otherwise reject/separate mixed-source inputs before they can poison one turn. Add a regression test that queues a Slack follow-up, accepts a non-Slack follow-up while dispatch is held, and verifies that the two source boundaries do not produce a permanently failing mixed turn.

## Prior finding dispositions

- **Prior MF-1, source-less compatibility rejected the pre-change Skill snapshot:** fixed properly. `readExecutionSourceContext` now distinguishes a genuinely absent discriminator from an explicit null (`packages/runner/src/runtime/slack-execution-context.ts:56-74`), routes source-less compatibility through a separate self-consistency parser, and keeps explicit `slack` on the published identity/hash validator. The actual pre-change fixture regression test passes.
- **Prior MF-2, explicit `non-slack` plus Slack context was relabeled as Slack:** fixed properly. `AgentJobInput.ExecutionSource` is nullable for legacy persisted inputs, `BuildDispatchAsync` reconciles only absent legacy source state, and explicit `non-slack` with context throws (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Dispatch.cs:16-22,69-90`). The Server dispatch specs for explicit mismatch and absent-source legacy reconciliation pass.

## Dimension verdicts

- **Issue acceptance criteria:** FAIL due MF-1. The canonical Skill, direct-question and silent-recovery rules, initial/follow-up Slack injection, non-Slack single-source envelope, and fail-closed invalid-context paths are otherwise checked with no additional issue.
- **Coverage:** FAIL. The changed tests cover the canonical asset and digest lock, Slack DM/channel/thread anchors, initial/follow-up Skill parity, batched same-source representative selection, source/context validation, legacy mixed-version parsing, invalid AgentJob/follow-up rejection, and non-Slack preservation. They do not cover a queued Slack turn receiving a non-Slack follow-up before dispatch.
- **Correctness:** FAIL due MF-1. The two findings from the previous review are fixed, but the new source-consistency check exposes an unhandled mixed-source batching path that violates the non-Slack preservation criterion.
- **Consistency with surrounding codebase and conventions:** checked, no additional issue. The implementation follows the existing AgentJob, Session, Runner capability, managed Skill, and follow-up dispatch boundaries; the finding is a missing source boundary in the existing batching decision.
- **Tests and verification:** FAIL for completeness. `npm run test:ci --prefix packages/runner` passed 155 files and 1,680 tests. `dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj --no-restore` passed 3,081 tests. The 14 directly affected Server specs passed, including the source mismatch, legacy reconciliation, anchor, parity, and batching cases. The full Server SpecTests run passed 3,699 of 3,700; its single failure is the unchanged `Specs/Auth/PatTokenSpecs.cs` fixed-expiry test and is unrelated to this change.

## Observations

- The full Server SpecTests failure is an existing clock-sensitive `PatTokenSpecs.Create_WithExplicitTtl_ExpiresExactlyThatFarOut` assertion (`packages/server/tests/Mohist.Server.SpecTests/Specs/Auth/PatTokenSpecs.cs:138-150`); the Auth test path is unchanged by this issue. It does not affect the issue verdict.
- Server and Runner independently pin the same published Skill digest. Current contract tests protect the value on both sides, but a future cross-project fixture check would reduce drift risk.

## Verdict

**FAIL** - MF-1 leaves a supported mixed-source follow-up workflow incomplete and can leave accepted inputs permanently queued.

<promise>FAIL</promise>
