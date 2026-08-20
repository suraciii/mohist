# Review - Issue 617

## Must-fix findings

### MF-1 - Compatibility mode rejects pre-change Slack payloads

**Criterion violated:** T-002 acceptance criterion 9 in `tasks.json`, and the legacy rollout scenario in `specs/slack-skill-injection/spec.md` (the upgraded Runner must accept pre-existing source-less work through the bounded legacy path while strict validation is disabled, preserving its prior behavior).

**Where:** `packages/runner/src/runtime/slack-execution-context.ts:57-67,83-125`, reached from `packages/runner/src/runtime/agent-job-executor.ts:95-102` and `packages/runner/src/server/followup-handler.ts:112-119`.

**Problem:** `readExecutionSourceContext` identifies an omitted source as `legacy`, but it calls the current `readSlackExecutionContext` before making that decision. That parser requires the current published v1 identity and hash (`PUBLISHED_SLACK_SKILL_HASH`). A source-less dispatch produced by the pre-change Server can contain the previous `1.0.0` Skill snapshot: the old embedded asset hashes to `de3272639a1d390f3dcf915e65b6c057bf0b9eb91c51545572eb1e484c8c1a22`, while the new Runner accepts only `dedf18a796543ade06a9e0ece00c086577153e1e633f868c099b01cf910d641b`. The payload is therefore classified as invalid before the legacy path can run. This affects both an in-flight/queued AgentJob and an old follow-up during a rolling deployment.

**Required disposition:** Separate legacy compatibility parsing from v1 validation. With strict validation disabled, a genuinely pre-existing source-less payload must retain the prior source-less behavior (including a valid old Skill snapshot), while explicit v1 `slack`/`non-slack` payloads must continue to use the current published validator. Add a mixed-version regression test using the pre-change source-less Slack context, not only a source-less payload containing the new fixture.

### MF-2 - Server silently relabels a non-Slack/context mismatch as Slack

**Criterion violated:** Issue acceptance criterion 1 (non-Slack execution must receive neither the Slack Skill nor the reply anchor), plus T-002 acceptance criterion 4 and the source/context contract in `specs/slack-skill-injection/spec.md` (a `non-slack` source carrying Slack context is invalid).

**Where:** `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Dispatch.cs:19-22`.

**Problem:** When an `AgentJobInput` has `ExecutionSource == non-slack` and a non-null `SlackExecutionContext`, `BuildDispatchAsync` changes the effective source to `slack` and sends both the context and Slack Skill. The Runner never sees the mismatch, so its validator cannot reject it. For example, a valid Server-created context accidentally paired with an explicit non-Slack source executes as Slack work instead of failing closed. The comment says this is legacy reconciliation, but `AgentJobInput.ExecutionSource` defaults to `non-slack`, so the current representation cannot distinguish an old persisted input whose source field was absent from a new malformed input that explicitly says `non-slack`.

**Required disposition:** Preserve an explicit source/context mismatch as a failure. Distinguish an absent legacy source from an explicit `non-slack` value (or otherwise carry an unambiguous legacy marker), and only reconcile the absent legacy case from trusted durable Slack context. Add a Server dispatch test proving that explicit `non-slack` plus Slack context is rejected and that the legacy absent-source case does not get relabeled as ordinary non-Slack work.

## Verdict

**FAIL** - the two must-fix findings above leave the compatibility contract and fail-closed source/context boundary incomplete.

## Dimension verdicts

- **Issue acceptance criteria:** FAIL. Fresh v1 Slack initial and follow-up dispatches carry the same locked Skill and anchor, explicit malformed v1 contexts are rejected, and current non-Slack envelopes omit Slack injection. MF-2 is a direct failure of the non-Slack exclusion when the source/context pair is malformed; MF-1 prevents pre-change Slack work from continuing during the rollout required by the plan.
- **Coverage:** FAIL. The current tests cover the canonical asset, current v1 context mutations, DM/channel/thread anchors, batched representative selection, current initial/follow-up parity, explicit Slack-without-context rejection, and current non-Slack envelope preservation. They do not cover the pre-change source-less Slack payload required by the mixed-version criterion or the Server-side explicit non-Slack/context mismatch.
- **Correctness:** FAIL for MF-1 and MF-2. The normal current-version path is internally consistent, but the two adversarial boundary cases above do not fail or route as specified.
- **Consistency with surrounding codebase:** checked, no additional issue. The implementation uses the existing AgentJob, Session, Runner control, managed Skill, and execution-envelope seams; the problems are contract-boundary behaviors described above rather than unrelated style or architecture concerns.
- **Tests and verification:** FAIL for coverage completeness. Verification run during this review: Runner production and test typechecks passed; 60 focused Runner tests passed; `Mohist.Server.UnitTests` passed 3,081/3,081; `SlackReplyAnchorIngressSpecs` passed 3/3; `AgentSessionFollowupGrainSpecs` passed 38/38. The focused suites pass, but they do not exercise either must-fix case. A generic filtered full SpecTests command was not usable because this repository's Microsoft Testing Platform ignores the VSTest filter; the affected classes were run directly with the xUnit v3 runner.

## Observations

- `readExecutionSourceContext` treats an explicit `executionSource: null` the same as an omitted source when strict validation is disabled (`packages/runner/src/runtime/slack-execution-context.ts:59`). New Server producers emit a concrete value and strict mode rejects it, so this is not a separate must-fix, but the compatibility implementation should distinguish omitted legacy fields from malformed explicit values.
- The Server and Runner use the same published Skill hash as an independently maintained constant/fixture. The catalog lock and current parity tests protect the present bytes; an automated cross-project fixture check would reduce future drift risk.

<promise>FAIL</promise>
