# Review — Issue 634 (re-review)

## Verdict: FAIL

## Must-fix findings

### MF-1 — The new policy-matrix specs race the shared Slack API fake and make the required verification nondeterministic

**Violated criteria:** Issue AC 9 and T-003's requirement for deterministic fixed-time authorization coverage and a passing Server test suite.

`SlackMultiAgentIngressSpecs` now obtains the process-wide `SlackApiTestScript` singleton from `MohistIntegrationFixture` and mutates `Responder`/`Requests` throughout the ten-case policy matrix (`packages/server/tests/Mohist.Server.SpecTests/Specs/Slack/SlackMultiAgentIngressSpecs.cs:421-515`). The class is not assigned to a serialized collection or an isolated fixture. Other independently scheduled classes, including `SlackAccessPolicySpecs`, use the same singleton. The test infrastructure explicitly permits default per-class parallel scheduling (`packages/server/tests/Mohist.Server.SpecTests/Support/MohistCollections.cs:25-27`).

This is not only a theoretical race: two consecutive current-tree SpecTests runs failed the unrelated `SlackAccessPolicySpecs.Anyone_workspace_member_in_a_bot_channel_is_accepted` assertion because it expected the two scripted Slack requests but observed zero. The newly expanded matrix can clear or replace the shared script while that other test is using it. Thus the disposition does not deliver deterministic policy verification, and the full suite is not reliably green.

Isolate the policy matrix's Slack API script or serialize every test class that shares this singleton under one collection, and make responder cleanup safe even when a test assertion fails. Verify the full SpecTests suite repeatedly after the isolation change.

### MF-2 — A migrated pre-fact claim can retry into an interactive chooser that is guaranteed to be stale

**Violated criteria:** The prompt requirement that a winner retry after a lost delivery remains usable and idempotent, and the T-002/issue requirement that a claim lacking retained facts or complete candidate references cannot silently degrade into a chooser or execution path.

The additive migration intentionally leaves old rows as `SelectionState=Pending`, `AmbiguityKind=Legacy`, and `CandidateReferencesJson=[]` (`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260913000000_AddSlackSelectionFacts.cs:36-57`). On a redelivery where the old race winner never persisted its outbox delivery, `SlackAmbiguousPromptStore.TryClaimAsync` treats the same winning Connection as claimed again whenever the stable outbox row is absent, without rejecting the legacy/incomplete snapshot (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackAmbiguousPromptStore.cs:145-156`). `HandleAmbiguousPromptAsync` checks only that the snapshot is still `Pending`, then renders and enqueues buttons from the current event facts (`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.ChannelIngress.cs:418-449`).

That chooser cannot be accepted: click handling requires the payload's non-legacy ambiguity kind and candidate set to exactly equal the durable claim, whose migrated values remain `Legacy` and `[]` (`packages/server/src/Mohist.Server/Slack/Services/SlackAgentSelectionService.cs:588-606`). Every button therefore resolves to `stale_action`. This is the exact lost-delivery retry window the once-only claim is meant to recover, but after migration it produces a broken interactive chooser instead of settling or visibly rejecting the incomplete legacy claim.

The new migration test does not catch this. Its "cannot execute" check calls `TryDecideAsync` with empty execution ids (`packages/server/tests/Mohist.Server.UnitTests/Slack/SlackSelectionMigrationTests.cs:59-68`), so it only proves ordinary argument validation; it does not exercise same-winner ingress redelivery, chooser rendering, or selection handling for the migrated row.

Ensure incomplete/legacy claims cannot be reclaimed into an interactive chooser—consistent with the plan's no-backfill decision, they should be surfaced and settled/ignored until cleanup rather than reconstructed from a redelivery—and add a migration-to-ingress test for an existing claim whose original outbox delivery is absent.

## Previous finding dispositions

- **Previous MF-1 — `ThreadLaunch`/`Bound` recovery could complete without Session acceptance:** fixed. The effective bound Session and `ThreadFollowup` dispatch are persisted before follow-up inbox acceptance, recovery distinguishes `LaunchThread` from `FollowupThread`, and an existing follow-up inbox row is replayed through `AcceptFollowupAsync` before completion (`SlackAgentSelectionService.cs:357-387`, `420-463`, `503-542`). The new crash-window spec verifies the retained input and turn reach the bound Session.
- **Previous MF-2 — deterministic acceptance coverage was incomplete:** partially fixed. The change adds live signed tests for already-bound follow-up, unbound thread launch, multi-bound reply follow-up, the requested mutable-policy matrix, migration, and worker retention. However, MF-1 makes the policy matrix nondeterministic, and MF-2 shows the migration coverage misses a real legacy-redelivery failure mode.

## Re-review checks

- **Every previous finding:** checked; dispositions are recorded above.
- **Regression check:** FAIL — MF-1 is a test-isolation regression introduced by the expanded policy matrix.
- **Coverage:** FAIL — the migration test does not cover the migrated same-winner/lost-outbox retry path in MF-2.
- **Correctness:** FAIL — MF-2 can present an interactive chooser whose every signed choice is guaranteed to be rejected as stale.
- **Consistency with surrounding codebase:** checked. The bound-race recovery fix now follows the provider inbox's actual persist-before-Session-acceptance semantics. The remaining consistency problem is the unsynchronized use of a shared mutable test fake.
- **Tests:** FAIL. The UnitTests run passed 3800/3800. Two consecutive current-tree SpecTests runs each failed 1/3088 at `SlackAccessPolicySpecs.Anyone_workspace_member_in_a_bot_channel_is_accepted` (expected two Slack API requests, observed zero), consistent with MF-1. `git diff --check` passed.

## Observations

1. `Signed_selection_route_follows_up_the_already_bound_thread_with_retained_files` proves that file metadata survives in `FilesJson` and that the text input is accepted, but it does not assert the resulting input's attachment acceptance/results. The implementation does pass retained files through `SlackAttachmentInputBinder`; a stronger assertion would more directly pin the attachment-provenance criterion.

<promise>FAIL</promise>