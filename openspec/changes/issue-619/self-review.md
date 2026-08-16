# Self-Review (round 2) - Issue 619

Reviewer stance: reviewer, not fixer. This is a re-review against the current issue record read with `mo issue view 619 --project proj_f6c141d63b6243bfbb481737b2243b87 --json body,comments,attachments,feedback,updatedAt` and the current plan artifacts. The issue has four acceptance criteria: same-conversation unavailability guidance, privileged diagnostic detail with caller-safe redaction, deduplication across Slack redelivery, and no AgentJob/Session/SessionInput for an unready launch.

## Prior Findings

- **F1 fixed - non-disabled Connection unavailability is now in scope.** `proposal.md:7-12,20-23`, `design.md:13-19,40-44,66-68`, `spec.md:1-4,22-43,76-93,108-117`, and `tasks.json:T-001.acceptanceCriteria[1-3,8-9]` now define the enabled, non-disabled unavailable states: incomplete setup, unhealthy/degraded health including service-offline and backpressure, and `OfflineGapAt`. They specify Connection-gate precedence, the existing `backpressured` code, `connection_unavailable` for other covered states, the required safe nudge, deduplication, no executable resources, and the unchanged Disabled audited-discard path. The missing issue goal is covered and assigned to implementation and tests.

- **F2 fixed - first mentions in unbound channel threads are now explicit new-launch paths.** `design.md:7,40,42,52,66`, `spec.md:16-20,35-38,90-101`, and `tasks.json:T-001.acceptanceCriteria[1,3-4,6,9]` identify the first mention for this Connection in an unbound thread as a new launch. The plan gates it before thread-history reads and executable side effects, preserves `body.ThreadTs` as the nudge target, and includes targeting, deduplication, resource-isolation, and normal-flow coverage. Bound-thread follow-ups remain exempt.

No prior must-fix finding remains unaddressed. The amended sections are consistent with each other and do not regress the issue's Disabled exception or the existing-session distinction.

## Must-Fix Findings

None.

## Observations

- **O1 - Diagnostic API versus UI scope could be made more explicit.** `tasks.json:T-002` requires the authorized Slack Connection diagnostic/read response to expose the full canonical result, but it does not name changes to the Web diagnostic model or page. The current page renders legacy `agentReadiness` in `packages/web/src/pages/connection-diagnostic/ui/ConnectionDiagnosticPage.tsx:437-455`, while the Agent detail surface already renders canonical executability. This is not a must-fix for the issue because the existing privileged Agent surface and the planned authorized read response provide the required owner/operator view; the implementation should still clarify whether the Connection page is expected to render the new gaps and repair entry points.

- **O2 - Required-nudge backpressure has a state-side-effect edge case.** The live `SlackOutboxStore.EnqueueRequiredAsync` inserts a required `UserAction` even at capacity and then can flip `ConnectionHealth` to backpressured. The plan acknowledges this in `design.md:44,73`, while `design.md:60` and `tasks.json:T-002.acceptanceCriteria[4]` broadly say a Connection-unavailable admission does not mutate `ConnectionHealth`. The builder should define whether the backpressure transition is the intentional exception or whether the nudge path must avoid it. The issue does not require Connection state immutability, so this does not change the verdict.

- **O3 - Accepted-replay lookup is described as optional.** `design.md:40` says a read-only stable-identity replay lookup "may be used", but `design.md:75` and `tasks.json` notes rely on distinguishing previously accepted identities to preserve the existing route when readiness changes between deliveries. The implementation should make that lookup mandatory for accepted-replay preservation or explicitly narrow the preservation claim. The issue's four acceptance criteria concern blocked new admissions and do not independently require this state-transition behavior.

- **O4 - Follow-up behavior under Connection backpressure needs an explicit boundary.** The plan says existing DM and bound-thread follow-ups remain usable when Connection availability is blocked (`design.md:40`, `tasks.json:T-001.acceptanceCriteria[7]`), while the current route deliberately rejects backpressured follow-ups in `HandleDmIngressAsync` and `DispatchChannelFollowupAsync`. This is a plan/codebase compatibility question outside the issue's stated acceptance criteria; the task should state whether the existing backpressure rejection remains the exception.

## Dimension Verdicts

- **Issue goals and acceptance criteria - PASS, checked with no must-fix issue.** The plan now provides a Server-authored same-conversation nudge for blocked Agent and covered non-disabled Connection cases, safe fixed caller text, canonical privileged detail, stable per-message outbox identity, and a pre-admission resource boundary. It preserves the issue's Disabled non-goal, excludes accepted execution failures and Manager conversations, and does not alter Agent readiness rules.

- **Coverage - PASS, checked with no must-fix issue.** Coverage includes DM `Launch` and `NewTaskLaunch`, channel-root mentions, first mentions in unbound channel threads, bound-thread and DM follow-ups, Agent `not-configured` and `not-executable`, executable and unknown states, Disabled, setup-incomplete, unhealthy/degraded, service-offline, offline-gap, and backpressured Connections. It also covers root/thread targeting, redelivery, concurrency, uncertain delivery, diagnostics, documentation, and resource counts.

- **Correctness - PASS, checked with no must-fix issue.** The proposed ordering classifies and validates before gating, checks Connection availability before Agent executability, avoids inbox/thread-history launch-context/attachment/workspace/session/liveness side effects in blocked branches, and routes normal and existing-session work through the current paths. The canonical `AgentReadinessService`, existing `UserAction` outbox kind, required-delivery uniqueness constraint, and existing retry/reconciliation lifecycle are used consistently.

- **Consistency with the current codebase and conventions - PASS, checked with no must-fix issue.** The plan matches the existing `SlackConnectionRoutes` DM/channel split, `AgentReadinessService` and `AgentInfo.Executability` contracts, `SlackOutboxStore.EnqueueRequiredAsync`, `SlackMessageIdentity.AsKey()`, Disabled audit branch, and outbox uniqueness model. No new dependency, persistence kind, or adapter-owned state is introduced.

- **Task breakdown, ordering, completeness, and verifiability - PASS, checked with no must-fix issue.** T-001 owns ingress gating, nudge production, documentation, resource isolation, delivery convergence, and ingress/outbox tests. T-002 depends on T-001 and owns the canonical diagnostic projection and authorization/state-independence tests. The spec anchors are current, the task graph is acyclic, and `jq empty openspec/changes/issue-619/tasks.json` passes.

## Overall Verdict

PASS - no must-fix problems; the plan is ready to build.

<promise>PASS</promise>