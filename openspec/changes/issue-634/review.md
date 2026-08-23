# Review — Issue 634 (re-review)

## Verdict: PASS

## Must-fix findings

None.

## Previous finding dispositions

### Previous MF-1 — shared Slack API fake made policy verification nondeterministic: fixed

`SlackAccessPolicySpecs` and `SlackMultiAgentIngressSpecs` now belong to the same non-parallel `SharedSlackApi` collection (`packages/server/tests/Mohist.Server.SpecTests/Support/MohistCollections.cs:26-30`; `SlackAccessPolicySpecs.cs:30-50`; `SlackMultiAgentIngressSpecs.cs:29-49`). Both classes clear the singleton `SlackApiTestScript` before and after each test, and the mutable-policy matrix also uses `finally` cleanup. Repository search found no other SpecTests consumer of the assembly fixture's singleton script. This removes the responder/request-reset race identified in the previous review.

The disposition is verified by the current full SpecTests run: 3089/3089 passed. The combined Server solution run also passed with the same 3089 SpecTests.

### Previous MF-2 — migrated pre-fact claim could render an unusable chooser: fixed

`SlackAmbiguousPromptStore.TryClaimAsync` no longer permits same-winner reclaim unless the existing durable snapshot has complete, non-legacy selection facts (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackAmbiguousPromptStore.cs:152-163`, `466-486`). Both ambiguous ingress branches detect an incomplete legacy snapshot before rendering, settle a still-Pending row as `legacy_missing_selection_facts`, and create no chooser or execution path (`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.ChannelIngress.cs:423-436`, `493-507`).

The new migration/store test applies the additive migration over a real pre-fact row, proves current redelivery facts do not reclaim or rewrite it, then verifies settlement and retention cleanup (`packages/server/tests/Mohist.Server.UnitTests/Slack/SlackSelectionMigrationTests.cs:16-111`). The route-level spec covers the same-winner/lost-delivery case and verifies no chooser outbox row, provider inbox row, Session, or AgentJob is created (`packages/server/tests/Mohist.Server.SpecTests/Specs/Slack/SlackMultiAgentIngressSpecs.cs:1189-1267`). The broken signed chooser from the previous review can no longer be produced.

## Re-review checks

- **Every previous finding:** checked; both prior must-fix findings are properly fixed as recorded above.
- **Regression check:** checked, no must-fix regression found in the isolation or legacy-fencing changes. Complete current claims retain the existing winner-retry behavior; only incomplete/legacy claims are prevented from reclaiming.
- **Coverage:** checked. The migration-to-redelivery path and shared-fake isolation are now covered in addition to the previously verified chooser, authorization, routing, concurrency, recovery, and retention matrix.
- **Correctness:** checked. Legacy rows remain inert and bounded by settlement/cleanup, while valid current snapshots still support stable-dispatch-ref retry. The test-collection change serializes all current users of the process-wide fake.
- **Consistency with surrounding codebase:** checked. Legacy settlement uses the existing selection-state transition and obligation-worker outcome/retention machinery; test isolation uses the repository's centralized xUnit collection definitions and per-test lifecycle cleanup.
- **Tests:** PASS. `dotnet test Mohist.Server.Tests.slnf --no-restore -p:SkipWebBuild=true` passed: Workflow Definition 178, Orleans 2, Arch 53, Unit 3800, Spec 3089. A separate full SpecTests run also passed 3089/3089. Server build passed, and `git diff --check master...HEAD` passed.

## Observations

1. The migrated-legacy route spec verifies the API result and the immediate absence of a chooser, but does not run the obligation worker to assert the eventual Slack-visible settlement message. The worker does process recent `Settled` rows and enqueue a stable generic outcome (`SlackAgentSelectionObligationWorker.cs:187-224`, `253-285`), so this is a possible test-strengthening improvement rather than an unmet criterion.
2. Go is unavailable in this workspace, so the Go adapter suite could not be rerun. The latest disposition changes no Go product code, and the Server-side route and full Server suites are green.

<promise>PASS</promise>