# Review — Issue 634 (re-review)

## Verdict: PASS

## Must-fix findings

None.

## Previous finding dispositions

### Previous MF-1 — shared Slack API fake made policy verification nondeterministic: remains fixed

The current change keeps `SlackAccessPolicySpecs` and all partials of `SlackMultiAgentIngressSpecs` in the same `SharedSlackApi` xUnit collection. Removing `DisableParallelization = true` from the collection definition does not reintroduce the original race: xUnit still serializes test classes within one collection, while only unrelated collections regain parallel scheduling (`packages/server/tests/Mohist.Server.SpecTests/Support/MohistCollections.cs:26-32`). Both shared-fake consumers still clear the singleton script before and after each test, and the mutable-policy scenarios retain `finally` cleanup.

The split of the selection tests into `SlackAgentSelectionSpecs.cs` and `SlackAgentSelectionRecoverySpecs.cs` uses partials of the same collected class, so those tests remain under the same collection and lifecycle isolation. The canonical gate passed all 3,093 Server SpecTests with the collection-parallel runner.

### Previous MF-2 — migrated pre-fact claim could render an unusable chooser: remains fixed

The subsequent commits do not change the legacy-claim fencing implementation. The migrated legacy redelivery scenario remains in `packages/server/tests/Mohist.Server.SpecTests/Specs/Slack/SlackAgentSelectionRecoverySpecs.cs:444-520`, verifying that a pre-fact claim with lost delivery is settled without rendering a stale chooser or creating execution resources. The production claim validation and ingress settlement behavior reviewed previously are unchanged.

## Re-review checks

- **Every previous finding:** checked. Both prior must-fix findings remain correctly disposed.
- **Regression check:** checked. The post-review production edits only move existing model configuration into `MohistDbContext.SlackModels.cs` and move `ResolveCanonicalFollowupTargetAsync` between partial `AgentSessionQuerier` files; their bodies are unchanged. The remaining edits reorganize and optimize tests without changing product behavior.
- **Coverage:** checked. The test split preserves the prior scenario set. Mutable candidate-state failures are now five independent Theory cases, retaining selected-policy denial, prompt-policy denial, expired selected lease, binding drift, and real soft deletion. Recovery still covers cross-Project restart, bound launch-to-follow-up races, the inbox/session crash window, transient retries, retention, and migrated legacy claims.
- **Correctness:** checked. Atomic fixture setup preserves the same valid Connection, managed App, credentials, and runtime-lease facts while reducing setup round trips. Directly seeded lease fingerprints match the production runtime fingerprint of concatenated `xapp` and `xoxb` credentials.
- **Consistency with surrounding codebase:** checked. The new partial files follow existing `MohistDbContext` and `AgentSessionQuerier` partial-class conventions. Shared-fake serialization uses the centralized xUnit collection definitions.
- **Tests:** PASS. `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj --no-restore -p:SkipWebBuild=true` succeeded. `dotnet test Mohist.Server.Tests.slnf --no-restore -p:SkipWebBuild=true` passed Arch 53/53, Unit 3,980/3,980, and Spec 3,093/3,093. The canonical `npm test` portfolio passed all seven tracks; its Server evidence passed 3,093/3,093 SpecTests with p95 331.8 ms under the unchanged 500 ms budget. `git diff --check master...HEAD` passed.

## Observations

1. Go is not installed in this workspace, so the Go adapter test suite could not be rerun. No Go file changed after the previous PASS review, and the current commits only reorganize Server code/tests and repair the Server duration gate.
2. `progress.txt` still says the shared collection has “parallelization disabled.” The current setup instead serializes the collection's member classes while allowing unrelated collections to run in parallel. This is workflow-artifact wording only and does not affect the product or acceptance criteria.

<promise>PASS</promise>