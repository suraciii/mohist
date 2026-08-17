# Self-Review: issue-620

## Review Mode

This is a re-review. I read the current issue with `mo issue view 620 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the artifacts. The issue requires: a Retry button with CLI-equivalent behavior under the clicker's permissions; visible rejection of expired, tampered, or other-operator actions; interactive single-Agent selection for ambiguous multi-Bot messages; and no duplicate execution on redelivery.

The previous reviews reported MF-001 through MF-006. I checked each disposition, then checked regressions and the retry, selection, authorization, adapter, persistence, recovery, and task-boundary contracts against the current codebase.

## Must-Fix Findings

### MF-006 - Selected-Connection authorization still has no implementable lease boundary

The issue's Product Shape requires every click to revalidate the operator's authorization, and the multi-Bot selection must start work only for the selected Agent. The selection capability spec makes this concrete: selection handling must revalidate selected Connection access authorization, including current policy and live Slack membership/channel checks.

The latest artifact change now says that selection evaluates both the prompt-owner Connection and the selected Connection, but it does not define how the selected Connection's lease-backed context is obtained. `design.md:46` says `SlackInteractionRoutes` passes one validated `SlackLeaseContext` from the interaction route. `design.md:106` and `tasks.json:57` then require a second evaluation using the selected Connection's "own lease-backed context" without defining a transport or server-side acquisition path for it.

That gap is concrete in the current code. `SlackInteractionRoutes.cs:33-38` validates the lease for the route's `{connectionId}` and `SlackInteractionRequest` carries only one `LeaseId` and `AdapterId` (`:90-102`). A selection button is signed and delivered by prompt-owner Connection A, so the click reaches A's route while the selected target may be Connection B. The existing `SlackLeaseContext` contains one lease identity (`SlackConnectionAccessDecider.cs:37-41`), and its resolver validates that lease for the target passed to `SlackConnectionAccessDecider` (`:219-228`). The ingress construction shows the same lease values are closed over for every target (`SlackConnectionRoutes.cs:535-538`). Reusing A's context to evaluate B therefore fails the target-specific lease check; omitting the context or falling back to A's lease either rejects valid allowlist/anyone selections or skips the selected target's live authorization boundary.

Failure case: an actor is authorized for prompt-owner A and selects eligible B. If B uses `allowlist` or `anyone`, the plan has no way to perform the required B live-member/channel check, so the valid selection cannot start B. If implementation instead treats A's successful check or stored candidate eligibility as sufficient, an actor whose current B policy or Slack membership denies invocation can start B. Either outcome violates the issue's selection criterion and the spec's requirement that only the selected Agent start under current authorization.

T-003 must specify a concrete target-specific authorization boundary: for example, how the Server obtains or validates B's runtime lease/capability before `EvaluateAsync`, or a different lease-aware access API and its adapter contract. It must state which connection owns the lease, how the interaction carries or resolves that proof, and how recovery behaves. The tests must include a valid non-owner selection for an `allowlist`/`anyone` selected Connection as well as selected-target owner-policy, allowlist, owner-transfer, live-member, and channel-absence denials, each proving no winner is committed and no dispatch occurs.

## Previous Finding Dispositions

- **MF-001: fixed.** `design.md` Decision 2, `proposal.md`, the Retry spec, and T-002 define `IAgentLauncher.LaunchConnectionRetryAsync`, the exact `slack-retry:{projectId}:{actionKey}` key, and persisted deterministic root Session/input/turn identities. The current launcher hard-codes the ordinary Slack key, so the newly specified retry-specific boundary is the necessary fix and no longer reuses the original message coordinator.
- **MF-002: fixed.** `design.md` Decision 7, both capability specs, and T-001/T-002/T-003 define the fixed-key `SlackActionRecoveryGrain`, persistent reminder, conditional recovery leases, pending-operation resume, and interaction replay behavior. The plan also separates the source-message provider-inbox fence from the button operation receipt.
- **MF-003: fixed.** `design.md` Decision 3, `proposal.md`, the Retry spec, and T-002 agree on the exact retryability allowlist and require the category-to-control test matrix. Missing, legacy, unknown, and non-allowlisted categories are explicitly text-only.
- **MF-004: fixed.** `design.md` Decision 2, the Retry spec, and T-002 define atomic Retry-only force-new-turn admission, pre-minted input/turn IDs, persistence of the follow-up operation ID, operation-targeted dispatch, and coverage for an unrelated queued follow-up.
- **MF-005: fixed.** `design.md`, `proposal.md`, the Retry spec, and T-001/T-002 require Retry authorization to re-evaluate the current receiving Connection policy and live Slack member/channel boundary through `SlackConnectionAccessDecider` and the validated `SlackLeaseContext`. The plan covers policy, allowlist, owner, and live-member changes for Retry. This does not resolve MF-006 because MF-005 applies to Retry's receiving Connection, not the selected Connection in the separate selection flow.
- **MF-006: not fixed.** The new wording adds the expected selected-Connection check and denial tests, but still does not provide the selected target's lease/capability path. The acceptance text is therefore not implementable against the current interaction envelope and lease service.

No regression was found in the fixes for retry identity, recovery liveness, retryability policy, threaded retry isolation, or Retry authorization. Existing signed Stop behavior remains explicitly preserved.

## Dimension Checks

### Issue Goals and Acceptance Criteria

**FAIL due to MF-006.** Retry behavior, visible invalid-action outcomes, interactive selection, and redelivery deduplication are represented. The selection path does not provide a usable current-authorization check for the Connection whose work it starts.

### Coverage

**FAIL due to MF-006.** The plan lists selected-Connection denial scenarios, but it does not cover the lease/capability needed to perform those checks, nor a valid selected-target allowlist/anyone path. The other issue criteria are covered.

### Correctness

**FAIL due to MF-006.** The Retry state machine is coherent with the current launcher, session, dispatcher, access-decider, inbox, and outbox boundaries. The selection state machine either rejects valid selected-target live checks because it has only the prompt-owner lease or risks dispatching after checking only the prompt owner.

### Consistency With the Current Codebase

**FAIL due to MF-006.** The current route validates one connection-scoped runtime lease, and `SlackLeaseContext` resolves a target-specific token only through that one lease. The plan names the current access service but does not define a compatible selected-target interaction boundary.

### Task Breakdown, Ordering, and Verifiability

**FAIL due to MF-006.** T-001 -> T-002 -> T-003 is a workable dependency order, and the recovery ownership split is clear. T-003's selected-Connection acceptance criterion cannot be verified until the task specifies how its target-specific lease/capability is supplied and tested.

## Observations

- Retry's wrong-message validation needs a concrete presentation identity contract. The Retry payload is described as binding the original source message, while the button is rendered in the replaceable Slack status message whose provider message identity may differ and is learned through outbox delivery. Unlike selection, the plan does not explicitly persist or bind that Retry presentation identity. This should be resolved so the stated wrong-message test cannot either reject a valid status-button click or accept a copied action in another message.
- The ambiguous prompt is claimed before a Session/input exists, while the current attachment binder binds accepted files to a Session/input owner. The plan requires accepted attachments in the durable source snapshot but does not choose the attachment ownership/lifecycle for ambiguous ingress. Text-only ambiguity is still covered; attachment-bearing ambiguity needs an implementation decision.
- Retry and selection lifetime, the candidate-count limit for Block Kit, and retention of expired operation/prompt rows remain open questions. They are operational/product decisions rather than must-fix problems for the stated normal flow because the plan already requires expiry and a readable fallback.
- The Retry allowlist is narrower than the issue's unqualified phrase "failed notification." The plan makes that policy authoritative and tests it consistently; expanding the allowlist should be a separate product decision unless the issue is broadened.

<promise>FAIL</promise>
