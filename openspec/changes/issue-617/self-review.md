# Self Review

## Verdict
FAIL

This is a re-review. I reread issue 617 with `mo issue view 617 --project proj_f6c141d63b6243bfbb481737b2243b87`, then reviewed `proposal.md`, `design.md`, `tasks.json`, and both specification files against the issue's goals and acceptance criteria.

## Must-Fix Findings

### 1. The pinned digest is for the current incomplete Skill, not the Skill this plan requires

**Violates:** issue acceptance criteria 2, 3, and 4; `specs/slack-collaboration-skill/spec.md:17-30`; `tasks.json:T-001`, acceptance criteria 11-15.

The plan pins version `1.0.0` to `de3272639a1d390f3dcf915e65b6c057bf0b9eb91c51545572eb1e484c8c1a22` (`design.md:40`; `tasks.json:T-001`, criterion 14). That is the SHA-256 of the current embedded file `packages/server/src/Mohist.Server/Agent/Services/Assets/mohist-slack-collaboration.skill.md`, whose content is only 16 lines. The current asset covers sending, silence, self-contained replies, delegation, and anchor use, but does not state that a direct human question overrides silence or that restart, Session recovery, and compaction must resume silently. Those required rules are explicit in `docs/slack.md:446-461` and in the issue.

T-001 simultaneously requires updating the asset to include those missing rules and rejecting any changed bytes under the existing version. Any required wording change produces different instruction bytes and therefore a different SHA-256. With the current pinned mapping, the catalog will reject the corrected asset; retaining the current bytes leaves the issue's direct-question and silent-recovery criteria unmet. Define the final canonical asset first, compute its exact UTF-8 digest, and update the version lock, contract fixtures, and parity expectations to that digest. The plan must not use the hash of the pre-change asset as the published v1 hash.

### 2. The required source discriminator is deployed in an order that breaks existing dispatches

**Violates:** issue acceptance criterion 1's non-Slack preservation requirement; `specs/slack-skill-injection/spec.md:79-92`; `design.md:11, 76, 101-110`; `tasks.json:T-002`, acceptance criterion 37.

The revised plan makes an omitted or unknown `executionSource` invalid and requires `non-slack` for ordinary work (`design.md:76`; `tasks.json:T-002`, criteria 34-35 and 37). Its migration plan nevertheless deploys that strict Runner before the Server starts emitting the discriminator (`design.md:110-111`; `tasks.json:T-002` notes). The current Server does not emit one: `packages/server/src/Mohist.Server/Contracts/RunnerControlContracts.cs:77-84` has no source field in `FollowupParams`, and `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Dispatch.cs:31-60` emits no source in the AgentJob payload.

During that rollout interval, the new Runner will reject every existing source-less non-Slack AgentJob before runtime selection and every existing source-less follow-up at the control dispatcher. This regresses Web, CLI, Workflow, and other non-Slack execution even though the plan promises to preserve it. The same transition also makes old Slack dispatches fail before the new Server can provide their context. Change the migration to an explicit compatibility or atomic-cutover sequence: for example, first deploy Server emission while the old Runner is proven to ignore the new control field, then enable the strict Runner validator, or use a bounded feature gate that preserves the source/context fail-closed invariant after activation. Add mixed-version rollout tests and do not solve this by treating an unmarked dispatch as non-Slack after strict validation, because that would reopen the earlier Slack-to-ordinary-work failure.

## Prior Finding Dispositions

- **MF-1, Slack context could degrade to ordinary work:** fixed in the plan. `design.md:51-57, 76-80`, `specs/slack-skill-injection/spec.md:1-2, 48-72`, and T-002 criteria 31, 34, and 35 now require a persisted `executionSource`, enforce the source/context pair, and cover omitted/null Slack contexts on both paths. Finding 2 above is a rollout regression introduced by this repair, not a residual omission of the discriminator.
- **MF-2, batched follow-ups had no complete anchor:** fixed in the plan. `design.md:53-55`, `specs/slack-skill-injection/spec.md:25-37`, and T-002 criteria 32-33 and 38 define the persisted DM root and first durable `InputId` representative, with corresponding tests.
- **MF-3, same-version Skill immutability and parity were not enforceable:** fixed in the plan's mechanism. `design.md:40, 57, 92-94`, `specs/slack-collaboration-skill/spec.md:1-15`, and T-001 criteria 14-15 add a pinned mapping, drift rejection, and initial/follow-up parity. Finding 1 identifies that the selected pinned value is the hash of the wrong, pre-change payload.

## Dimension Verdicts

- **Issue goals and acceptance criteria:** checked against the issue before the artifacts; the two must-fix findings violate the direct-question/silent-recovery, Skill integrity, and non-Slack preservation requirements.
- **Coverage:** incomplete until the canonical post-change Skill digest and mixed-version rollout behavior are defined and tested.
- **Correctness:** the source/context and batched-anchor repairs are directionally correct, but the current digest makes the required Skill impossible to publish and the stated deployment order causes existing work to fail.
- **Current codebase consistency:** checked against the current Server-to-Runner contracts, AgentJob dispatch builder, Runner control dispatcher, and execution entry point; the rollout finding follows from their current source-less payloads and absent-context behavior.
- **Task breakdown, ordering, and verifiability:** T-001 and T-002 are logically ordered for implementation, but T-001 lacks a valid final content/hash lock and the migration ordering is not executable without a compatibility or cutover step.

## Observations

- The design says the Runner rejects a "non-canonical digest" (`design.md:76`) and T-002 repeats that requirement, but the specification only defines matching the supplied instruction bytes. Clarify whether Runner pins the published v1 digest or relies on the declared trusted Server-to-Runner transport; the latter detects accidental body/hash mismatch but not a changed body paired with a newly computed hash.
- `docs/slack.md` is declared authoritative, but the task does not require an automated document-to-asset parity check. The asset content test should either compare the six documented rules directly or state the reviewed canonical mapping clearly enough to prevent future drift.
- The issue describes the change as not altering persistence, while the revised design adds append-only source/root provenance to durable AgentJob/Session state (`design.md:32, 107`). The plan explains that Session queue/turn semantics remain unchanged; the intended interpretation of the issue's persistence constraint should still be made explicit.

<promise>FAIL</promise>
