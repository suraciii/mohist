# Self-Review: issue-620

## Review Mode

This is a re-review. I read the current issue with `mo issue view 620 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the artifacts. The issue goals and acceptance criteria are:

1. A Retry button on a failed notification completes one retry with the clicker's permissions and CLI-equivalent effect.
2. Expired, tampered, or other-operator buttons are rejected with a visible message.
3. An ambiguous multi-Bot message gets an interactive choice, and selecting one Agent starts work only for that Agent.
4. Button clicks do not cause duplicate execution under redelivery.

The previous review reported MF-001 through MF-003. I verified those dispositions, then checked for regressions and for failures in the root/thread retry, authorization, selection, adapter, and task-boundary contracts.

## Must-Fix Findings

### MF-004 - Threaded Retry does not guarantee a new follow-up turn

`design.md` Decision 2 and `specs/slack-failure-retry-action/spec.md` require a threaded Retry to create a new follow-up input and turn. T-002 repeats that requirement in its third acceptance criterion. The plan says to call the existing `AcceptFollowupAsync` and then `AgentSessionFollowupDispatcher`, but it does not add a force-new-turn or targeted-dispatch contract.

The current implementation makes the failure concrete. `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:574-628` rejects only unaccepted pending follow-ups. `packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:1258` then intentionally reuses an existing queued turn for a text-only follow-up. A failed turn can coexist with an already accepted, queued follow-up, so a Retry can be appended to that unrelated turn instead of getting its own turn. After that, `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1031-1097` selects the first queued turn, and `packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:25-67` has no operation/turn target to override that selection.

Failure case: a failed threaded turn has an accepted queued follow-up; the Retry operation accepts its source text under the retry key; the session appends it to the existing queued turn; the dispatcher sends the combined or unrelated turn. The button therefore does not perform one isolated retry of the failed input and cannot guarantee the issue's first acceptance criterion or the plan's fresh-follow-up contract. T-002 must define either an atomic force-new-turn and operation-targeted dispatcher/resume path, or an explicit pre-accept rejection as unavailable when a queued follow-up would cause coalescing. The tests must cover this queued-follow-up case and verify the returned input and turn identities.

This was a pre-existing problem missed in the first review: that review examined the root `LaunchConnectionAsync` identity boundary, but did not trace the follow-up assignment rule and the dispatcher’s first-queued-turn behavior.

### MF-005 - Retry authorization does not require the clicker's current Connection permission

The issue explicitly requires the Retry to run with the clicker's permissions and says that another operator's button is rejected. The established Slack design says that every click revalidates actor authorization and that pressing an action performs the operation under the presser's authority (`design/slack.md`, Signed Action Buttons; `docs/slack.md:312`). The current access boundary at `packages/server/src/Mohist.Server/Slack/SlackConnectionAccessDecider.cs:46-66,95-155` reads the current access policy, allowlist, and live Slack member state on every invocation.

The plan binds the Retry payload to an actor and requires an authorization check, but `design.md` Decision 2 step 1 and T-002 acceptance criterion 2 never require re-evaluating that actor through the current Connection access policy/member boundary. They only establish the original Slack provenance and the bound actor. The concrete existing action-control predicate in `packages/server/src/Mohist.Server/Slack/Services/SlackTurnControlService.cs:240-242` is owner-or-session-initiator, which is not equivalent to current Connection invocation permission.

Failure case: an allowlisted sender starts work and is later removed from the allowlist, or the Connection owner changes while the old owner remains the session initiator. A Retry action still matches its bound actor, and an owner-or-initiator check accepts it even though the clicker's current Connection permission would deny the equivalent new invocation. This violates the issue's first criterion and the product's per-click authorization rule. T-002 must specify the action-specific current access check, including how the valid adapter lease is used for live-member checks, while preserving the bound-actor rejection for a different operator. Tests must cover access-policy changes, allowlist removal, owner transfer, and live-member denial after the failure notice was rendered.

This was also missed in the first review: the first review credited the presence of actor/context checks and did not compare the planned Retry authorization to the repository's current `SlackConnectionAccessDecider` semantics or the presser-authority rule in `design/slack.md`.

## Previous Finding Dispositions

- **MF-001: fixed.** `design.md` Decision 2, `proposal.md`, the Retry spec, and T-002 now define `IAgentLauncher.LaunchConnectionRetryAsync`, the exact `slack-retry:{projectId}:{actionKey}` key, and persisted deterministic root Session/input/turn identities. The existing `AgentLaunchCoordinatorGrain` already accepts a distinct idempotency key and pre-minted identities, so the root boundary is implementable and no longer reuses the original Slack-message coordinator.
- **MF-002: fixed.** `design.md` Decision 7, both capability specs, and T-001/T-002/T-003 now define the fixed-key `SlackActionRecoveryGrain`, its persistent reminder, conditional recovery leases, pending-operation resume, and interaction replay behavior. The plan also explicitly separates the source-message provider-inbox fence from the button operation receipt, so a replay is not stopped by the original message's inbox row.
- **MF-003: fixed.** `design.md` Decision 3, `proposal.md`, the Retry spec, and T-002 agree on the exact retryability allowlist and require the category-to-control test matrix. Missing, legacy, unknown, and non-allowlisted categories are explicitly text-only. The earlier open policy question is resolved.

No regression was found in the fixes for the root identity, recovery liveness, or retryability policy, and the existing signed Stop behavior remains explicitly preserved.

## Dimension Checks

### Issue Goals and Acceptance Criteria

**FAIL.** All four issue criteria have corresponding plan sections, but MF-004 leaves threaded Retry behavior unable to guarantee one CLI-equivalent retry, and MF-005 leaves the required current clicker authorization undefined.

### Coverage

**FAIL due to MF-004 and MF-005.** Interactive multi-Bot selection, selected-Connection-only dispatch, visible invalid-action outcomes, durable operation identities, and redelivery recovery are covered. Retry coverage is incomplete at the threaded follow-up boundary and at current access-policy authorization.

### Correctness

**FAIL.** The root retry identity contract and recovery state machine are now coherent with the current coordinator. The threaded path is not correct for an existing queued follow-up, and the authorization contract can accept a previously authorized initiator after current permissions have changed.

### Current-Code Consistency

**FAIL due to MF-004 and MF-005.** The plan correctly reuses the launcher, session, outbox, and adapter boundaries, but its threaded Retry call does not match the session's coalescing semantics or the dispatcher’s first-queued-turn behavior. Its authorization wording does not identify the existing current-policy access service needed to match Slack's invocation rules.

### Task Breakdown, Ordering, and Verifiability

**FAIL.** T-001 -> T-002 -> T-003 is a workable dependency order, and the previous recovery work is assigned to T-002 with T-003 integration. T-002 still needs an explicit force-new-turn/targeted-dispatch task and a current Connection authorization task. Its existing tests mention fresh follow-up identity and actor authorization but do not force the two failure cases above, so they cannot make the acceptance criteria verifiable.

## Observations

- `design.md` leaves the Retry/selection lifetime and candidate-count limit open. The readable text fallback makes this an operational decision rather than a must-fix for the stated normal multi-Bot case.
- T-003 requires an authoritative source snapshot with accepted attachments, but the current ambiguous ingress claims a prompt before a Session/input exists and the current Slack attachment binder only binds against a Session/input owner. The implementation needs an attachment ownership/lifecycle choice; text-only ambiguous messages are not blocked by this observation.
- The issue describes accepted button clicks as durable provider interactions. The plan uses dedicated Retry/selection operation receipts instead of the existing source-message inbox. That is a reasonable way to keep button receipts separate from source-message deduplication, but the implementation should preserve equivalent auditability and capacity behavior.
- The exact retryability allowlist is narrower than the issue's unqualified phrase "failed notification". The plan explicitly decides this policy and tests it; whether more failure categories should become retryable belongs to a follow-up product decision unless the issue is broadened.

<promise>FAIL</promise>
