# Self-Review (round 1) — Issue 619

Reviewer stance: reviewer, not fixer. No prior `self-review.md` existed, so this is a first-review full sweep against the current Mohist issue record.

## Must-Fix Findings

### F1 — The plan omits the issue's non-disabled Connection-unavailable goal

**Must-fix.** The issue Product Shape says that admission must publish guidance when the Agent is not ready **or its Connection is unavailable**. The Non-Goals section excludes only `Connection Disabled`, which must retain its existing audited-discard behavior; it does not exclude unhealthy, offline, or other unavailable Connection states.

The plan narrows the feature to Agent executability in `proposal.md:7` and `design.md:7`, then explicitly says in `design.md:58` and `tasks.json:T-002.acceptanceCriteria[4]` that disabled, unhealthy, offline, and backpressured Connections retain their existing responses. The spec only covers the existing disabled/backpressure response in `spec.md:85-88`; it defines no Server-authored setup/unavailability nudge, stable per-message delivery identity, or no-execution-resource behavior for the other unavailable states.

Leaving this gap means a valid in-scope mention or DM can still receive no setup-mode guidance when the Connection is unavailable, violating the issue's Product Shape. The plan must define which non-disabled unavailable states are covered, add the corresponding nudge and side-effect/dedup contract, and assign implementation and tests while preserving Disabled's current semantics.

### F2 — First mentions inside unbound channel threads are not covered as new-launch paths

**Must-fix.** The issue scope says `mention` and DM admission paths, not only channel-root messages. The current state machine has an in-scope new-launch path for a first Bot mention inside an existing human discussion: `SlackConnectionRoutes.cs:1578-1613` reaches `ReadThreadHistoryIfAnyAsync` and then calls `LaunchChannelRootAsync` when there is no existing binding. This is a new Session launch, not an existing-session follow-up; the product documentation also describes it at `docs/slack.md:350-357`.

The plan repeatedly limits the path to a “channel-root mention/launch” (`proposal.md:20`, `design.md:7,40`, `spec.md:2,9-12`, and `tasks.json:T-001.acceptanceCriteria[1,3]`). Its targeting rule says a channel-root nudge has no thread target (`design.md:50`), and its tests enumerate only DM and channel-root cases. As written, a builder can satisfy the plan while leaving this existing new-launch route with the generic rejection or posting guidance at the wrong root instead of the triggering thread. That fails the issue's mention-scope goal and the plan's own “triggering thread or root” goal.

Add an explicit requirement/scenario and task acceptance coverage for an unbound-thread first mention: readiness must gate it before inbox/attachment/workspace/session effects, the nudge must target `body.ThreadTs`, and the same message identity must deduplicate it. Existing bound-thread follow-ups should remain exempt as the plan already intends.

## Observations

- **O1 — The operator UI projection is underspecified.** `tasks.json:T-002` requires a richer Slack Connection diagnostic response, but the current Web diagnostic model and page expose only legacy `agentReadiness` (`packages/web/src/entities/agent-connection/model/types.ts:11-29` and `packages/web/src/pages/connection-diagnostic/ui/ConnectionDiagnosticPage.tsx:437-455`). The plan names Server/API specs but no Web model, page, or UI test changes. The existing Agent detail surface already has canonical executability, so this is not independently a must-fix; it should be clarified whether the API response itself is the intended authorized surface or the Connection diagnostic page must render the gaps and repair entry points.
- **O2 — Final safe wording remains an open question.** `design.md:87` leaves localization and the not-configured/not-executable wording undecided. The safety and actionability contract is otherwise specified, so this is an implementation observation rather than a readiness blocker.
- **O3 — Delivery uncertainty is correctly limited to the existing at-least-once contract.** `design.md:70` acknowledges that a manual resend after an uncertain provider result can duplicate a Slack message, while the normative requirement concerns reuse of one durable intent and automatic recovery. This is consistent with the existing outbox model and does not create a must-fix finding for this issue.

## Full-Sweep Dimension Verdicts

- **Issue goals and acceptance criteria — FAIL.** The four checkbox criteria are addressed for blocked DM/channel-root Agent readiness: visible same-conversation guidance, safe versus privileged detail, message-identity deduplication, and no AgentJob/Session/SessionInput. F1 and F2 show that the broader Product Shape and mention scope are not fully covered.
- **Coverage — FAIL.** F1 leaves non-disabled Connection-unavailable admission uncovered; F2 leaves first mentions in unbound channel threads untested and ambiguously specified.
- **Correctness — FAIL.** The canonical readiness gate, required `UserAction` outbox row, existing uniqueness constraint, and pre-admission resource ordering are coherent for the covered Agent-blocked paths. They do not establish the required behavior for the two omitted paths above.
- **Consistency with the current codebase and conventions — PASS, checked with no convention issue.** The use of `AgentReadinessService`, `AgentInfo.Executability`, `SlackOutboxKinds.UserAction`, `EnqueueRequiredAsync`, the existing dispatch-reference uniqueness index, and the Disabled audit path matches current code. The findings are scope/coverage gaps rather than violations of those conventions.
- **Task breakdown, ordering, completeness, and verifiability — FAIL.** The T-001 to T-002 dependency is acyclic and the covered paths have concrete acceptance tests, but no task/spec scenario owns F1 or F2, so a builder can complete every listed task without proving the full issue goal.

## Overall Verdict

FAIL — must-fix findings F1 and F2 remain; the plan is not ready to build.

<promise>FAIL</promise>
