# Review - Issue 617

## Must-fix findings

None.

## Prior finding dispositions

- **Prior MF-1, source-less compatibility rejected the pre-change Skill snapshot:** fixed properly. Omitted `executionSource` now enters a separate legacy parser that checks the old self-consistent snapshot, while explicit `slack` and `non-slack` use the published v1 validator (`packages/runner/src/runtime/slack-execution-context.ts:63-88,97-149`). The mixed-version fixture test covers the actual pre-change digest (`packages/runner/src/runtime/slack-execution-context.test.ts:88-111`).
- **Prior MF-2, explicit `non-slack` plus Slack context was relabeled as Slack:** fixed properly. Nullable durable source state distinguishes an absent legacy field from an explicit source; only the absent-source plus trusted-context case is reconciled, while explicit `non-slack` with context throws (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Dispatch.cs:16-90`, `packages/server/tests/Mohist.Server.SpecTests/Specs/Agent/Grain/AgentJobDispatchEnvelopeSpecs.cs:25-80`).
- **Previous review MF-1, mixed-source follow-up batching could permanently block a Session:** fixed properly. New follow-ups store a source, queued-turn assignment now joins only inputs with the same effective source, and dispatch retains a defensive mixed-source check (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:1149-1293`, `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1052-1056`). The regression test proves Slack and non-Slack inputs receive separate turns and dispatches (`packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionFollowupGrainSpecs.cs:125-176`).

## Dimension verdicts

- **Issue acceptance criteria:** checked, no issue. Slack initial and follow-up paths carry the same locked Skill and complete Server-owned anchor; direct-question, no-empty-acknowledgement, and silent-recovery rules are present; non-Slack paths omit Slack controls; invalid source/context pairs fail closed before Runner execution or follow-up enqueue.
- **Coverage:** checked, no issue. Tests cover the canonical six-rule asset, digest drift, legacy compatibility, source/context mismatches, initial/follow-up anchor parity, DM and channel roots, batched representative selection, source-separated turns, invalid follow-up no-enqueue, and non-Slack preservation.
- **Correctness:** checked, no issue. The Server persists and reconstructs the source/root/provenance boundary, the Runner validates the published identity and exact UTF-8 digest, and the envelope adds the managed Skill and system facts only for validated Slack execution.
- **Consistency with surrounding codebase and conventions:** checked, no issue. The implementation uses the existing managed asset, AgentJob, Session, Runner capability, control-dispatch, and execution-envelope boundaries without changing Slack reply authorship or delivery ownership.
- **Tests and verification:** checked, no issue. `npm run test:ci --prefix packages/runner` passed 155 files and 1,680 tests. `dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj --no-restore` passed 3,081 tests. The affected Server spec classes passed 39 follow-up grain tests, 3 Slack anchor ingress tests, and 11 AgentJob dispatch tests. `git diff --check` passed and the embedded Skill hash matches the pinned digest.

## Observations

- `BeginNextFollowupDispatchAsync` validates source consistency and Slack provenance before `AgentSessionFollowupDispatcher` enters its delivery `try` block (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1052-1056`; `packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:45-83`). A malformed pre-existing durable turn could therefore remain queued and retry rather than be released. The current acceptance paths prevent new mixed turns, and invalid work is rejected before Runner invocation, so this is not a must-fix for Issue 617.
- The follow-up capability guard accepts `spec/*` as a test compatibility capability (`packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:38-43`). Production runners advertise `execution-source-v1`; ensuring `spec/*` cannot be used by a real non-v1 runner would further tighten the rollout boundary.
- The full Server SpecTests suite was not rerun in this round; the affected classes and both complete lower-level suites passed. The prior review recorded one unrelated fixed-expiry Auth failure in the full suite.
- Server and Runner independently pin the published Skill digest. The catalog and current parity tests protect the present bytes, but a cross-project fixture check would reduce future drift risk.

## Verdict

**PASS** - no must-fix problems remain; the change is ready to merge.

<promise>PASS</promise>
