# Self-Review: issue-620

## Review Mode

This is a re-review. I read the current issue with `mo issue view 620 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the artifacts. The issue requires: a Retry button with CLI-equivalent behavior under the clicker's permissions; visible rejection of expired, tampered, or other-operator actions; interactive single-Agent selection for ambiguous multi-Bot messages; and no duplicate execution on redelivery.

The previous review reported MF-001 through MF-003. The subsequent review reported MF-004 and MF-005. I checked all five dispositions, then checked regressions and the retry, selection, authorization, adapter, persistence, recovery, and task-boundary contracts against the current codebase.

## Must-Fix Findings

### MF-006 - Selected-Connection authorization is not defined or enforced

The issue's Product Shape requires every click to revalidate the operator's authorization, and the multi-Bot selection must start work only for the selected Agent. The selection capability spec makes this concrete: selection handling must revalidate **selected Connection access authorization**, not only the prompt owner.

`design.md` Decision 4 explicitly calls `SlackConnectionAccessDecider.EvaluateAsync` for the **prompt-owner Connection** and then says only that it will "recheck selected Connection eligibility." It never defines that eligibility as the selected Connection's current actor authorization, nor does it define how the selected Connection's lease-backed live-member/channel check is obtained. T-003's acceptance criterion 3 repeats the ambiguity with "selected-candidate eligibility, and current actor access" but does not identify the Connection to which current access applies. Its tests cover candidate invalidation, not selected-Connection policy, allowlist, owner-transfer, or live-member denial.

This is a real failure case in the current model. An actor can be authorized for prompt-owner Connection A while selected Connection B remains `owner_only`, removes the actor from its allowlist, or fails its live Slack membership/channel check. The plan's explicitly described authorization check for A can pass, while the unspecified B eligibility check can still commit and dispatch work for B. That violates the issue's per-click authorization requirement and leaves the selection behavior incomplete.

T-003 must specify the authorization boundary for the selected Connection, including the current `SlackConnectionAccessDecider` inputs and a valid lease/token-resolution path for that target, or explicitly define and justify a different cross-Connection authorization rule. The test matrix must include selected-target access-policy, allowlist, owner-transfer, and live-member/channel denial cases and verify no winner dispatch is committed.

## Previous Finding Dispositions

- **MF-001: fixed.** `design.md` Decision 2, `proposal.md`, the Retry spec, and T-002 define `IAgentLauncher.LaunchConnectionRetryAsync`, the exact `slack-retry:{projectId}:{actionKey}` key, and persisted deterministic root Session/input/turn identities. The current launcher hard-codes the ordinary Slack key, so the newly specified retry-specific boundary is the necessary fix and no longer reuses the original message coordinator.
- **MF-002: fixed.** `design.md` Decision 7, both capability specs, and T-001/T-002/T-003 define the fixed-key `SlackActionRecoveryGrain`, persistent reminder, conditional recovery leases, pending-operation resume, and interaction replay behavior. The plan also separates the source-message provider-inbox fence from the button operation receipt.
- **MF-003: fixed.** `design.md` Decision 3, `proposal.md`, the Retry spec, and T-002 agree on the exact retryability allowlist and require the category-to-control test matrix. Missing, legacy, unknown, and non-allowlisted categories are explicitly text-only.
- **MF-004: fixed.** `design.md` Decision 2, the Retry spec, and T-002 now define atomic Retry-only force-new-turn admission, pre-minted input/turn identities, persistence of the follow-up operation ID, operation-targeted dispatch, and coverage for an unrelated queued follow-up.
- **MF-005: fixed.** `design.md`, `proposal.md`, the Retry spec, and T-001/T-002 require Retry authorization to re-evaluate the current receiving Connection policy and live Slack member/channel boundary through `SlackConnectionAccessDecider` and the validated `SlackLeaseContext`. The plan covers policy, allowlist, owner, and live-member changes for Retry. This does not resolve MF-006 because MF-005 applies to Retry's receiving Connection, not the selected Connection in the separate selection flow.

No regression was found in the fixes for retry identity, recovery liveness, retryability policy, threaded retry isolation, or Retry authorization. Existing signed Stop behavior remains explicitly preserved.

## Dimension Checks

### Issue Goals and Acceptance Criteria

**FAIL due to MF-006.** Retry behavior, visible invalid-action outcomes, interactive selection, and redelivery deduplication are all represented. The selection path does not establish that the actor is currently authorized for the Connection whose work it starts.

### Coverage

**FAIL due to MF-006.** The plan covers the four issue criteria and the main selection scenarios, but selected-Connection authorization is not covered as an enforceable rule or test case. No other must-fix coverage gap was found.

### Correctness

**FAIL due to MF-006.** The Retry state machine is coherent with the current launcher, session, dispatcher, access-decider, inbox, and outbox boundaries. The selection state machine can authorize the prompt owner and then dispatch a different selected Connection without a specified current authorization decision for that target.

### Consistency With the Current Codebase

**FAIL due to MF-006.** The current `SlackConnectionAccessDecider` evaluates one specific `AgentConnection`, and live checks use a `SlackLeaseContext` whose resolver validates a target-specific runtime lease. The plan names the current service but only wires it to the prompt owner in the selection path; it does not define a compatible selected-target authorization boundary.

### Task Breakdown, Ordering, and Verifiability

**FAIL due to MF-006.** T-001 -> T-002 -> T-003 is a workable order, and the recovery ownership split is clear. T-003 needs an explicit selected-Connection authorization task and corresponding denial tests before the selection acceptance criteria are verifiable.

## Observations

- Retry's wrong-message validation needs a concrete presentation identity contract. The Retry payload is described as binding the original source message, while the button is rendered in the replaceable Slack status message whose provider message identity may be different and is learned through outbox delivery. Unlike selection, the plan does not explicitly persist or bind that Retry presentation identity. This should be resolved so the stated wrong-message test cannot either reject a valid status-button click or accept a copied action in another message.
- The ambiguous prompt is claimed before a Session/input exists, while the current attachment binder binds accepted files to a Session/input owner. The plan requires accepted attachments in the durable source snapshot but does not choose the attachment ownership/lifecycle for ambiguous ingress. Text-only ambiguity is still covered; attachment-bearing ambiguity needs an implementation decision.
- Retry and selection lifetime, the candidate-count limit for Block Kit, and retention of expired operation/prompt rows remain open questions. They are operational/product decisions rather than must-fix problems for the stated normal flow because the plan already requires expiry and a readable fallback.
- The Retry allowlist is narrower than the issue's unqualified phrase "failed notification." The plan makes that policy authoritative and tests it consistently; expanding the allowlist should be a separate product decision unless the issue is broadened.

<promise>FAIL</promise>