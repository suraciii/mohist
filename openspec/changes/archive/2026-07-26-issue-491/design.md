## Context

Issue #491 delivers two same-theme, independently-usable changes from `design/event-response.md`: (1) AgentJob terminal failure becomes externally visible via a new `com.mohist.agent.job.failed` event + a default-on notification; (2) approval decisions record a declarative operator `decidedBy`. Motivation and required behavior live in the proposal and specs; this doc covers how.

Current state relevant to the design:

- `AgentJobGrain` (`Agent/Grains/AgentJobGrain.cs:37`) is the authoritative owner of a standalone job's lifecycle. It persists via Orleans `[PersistentState]`, not a DB transaction, and has **no** `IEventStore` today. Every failure path funnels into one canonical entry point, `EnterTerminalStateAsync` (`:909`), which persists a `PendingSessionClose` obligation and registers a durable `agent-job-recovery` reminder. The reminder's `ReceiveReminder` (`:1077`) retries the durable Session-close delivery until acknowledged, then self-cleans. This self-healing obligation pattern (issue-449) is the template the failure-event emission reuses.
- Event emission elsewhere (`IssueGrain`, `WorkflowRunStore`) uses `IEventStore.AppendAsync(MohistDbContext, envelope)` inside a DB transaction. Critically, `IEventStore` also exposes `AppendAsync(CloudEvent, ct)` with no DbContext (`IEventStore.cs:17`) — it manages its own connection. This lets a grain-storage grain emit without taking on a DB transaction.
- `agentid` is already a first-class lineage key (`EventCatalog.cs:118`); the routing rule model carries `AgentId` (`Agent/Domain/RoutingRule.cs`); rule evaluation is a pure match-expression engine (`RoutingTableEvaluator.Evaluate`).
- Notification plumbing is uniform: a kind constant in `NotificationKinds` → default-on in `InboxSubscriptionState` and `HermesNotificationOptions.EnabledTypes` → subscription+resolve in `InboxProjectionHandler` and `HermesIssueNotificationHandler` → renderer branch. `WorkflowFailed` is the closest existing analog (terminal failure → inbox + Hermes).
- Approval has no operator field today: `ApprovalStatus` (`StageRun.cs:5`), `WorkflowRun.Approve/Reject` (`WorkflowRun.Approval.cs:104,124`), `StageApprovalResolved` (`WorkflowEvent.cs:40`), `WorkflowGrain.ApproveAsync/RequestChangesAsync` (`:252,260`), `ApprovalStatusView` (`WorkflowViews.cs:128`). The comment `author` (`MohistCliCommands.Issue.Comment.cs:18` → `IssueGrain.cs:1419`) is the exact declarative-author model to copy: required, trim, ≤100, declared not authenticated.

Per AGENTS.md the project is in active development with no version-compatibility constraint.

## Goals / Non-Goals

**Goals:**
- Make every AgentJob terminal failure (incl. preflight, timeout, retry-bound) emit a durable `com.mohist.agent.job.failed` event stamped with `agentid` + business lineage.
- Surface that failure in inbox and Hermes under a new default-on "Agent 响应失败" kind.
- Prevent an agent from responding to its own failure event, envelope-only.
- Thread a declarative `decidedBy` through approve/reject end-to-end so approval history distinguishes human from agent.

**Non-Goals (from issue + `event-response.md`):**
- Automatic response retry on failure (failure surfaces; retry = new event or human action).
- Frequency limiting / cooldown on the failure event.
- A→B→A two-agent mutual-response loop prevention (documented warning + dry-run visibility only; configuration responsibility).
- Authenticated operator identity (declaration model, like comment author).

## Decisions

### D1 — Emit the failure event from `AgentJobGrain.EnterTerminalStateAsync` as a durable terminal obligation

Emit at the single canonical terminal entry (`:909`), so every failure path (runner report, preflight, timeout, retry-bound, forced fail) is covered for free. Inject `IEventStore` into the grain and append via the **no-DbContext** `AppendAsync(CloudEvent, ct)` overload — the grain keeps its grain-storage persistence story; the event append is a separate durable write, not a DB transaction.

Make emission a durable obligation tracked in `AgentJobState`, paralleling `PendingSessionClose`: attempt the append on terminal transition; retry on each `agent-job-recovery` reminder tick until success; clear the obligation once appended. The event store dedups by `(source, id)`, so retries are idempotent. Poke the dispatcher after commit (`EventDispatcherPoke.PokeAfterCommit`). Only failed terminal states emit; completed states do not.

The reminder's self-clean condition (`ReceiveReminder :1090`) widens from "no pending session close" to "no pending session close **and** failure event emitted (or not a failure)" — this matters for jobs without an AgentSession, which have no `PendingSessionClose` and would otherwise unregister the reminder before the event append succeeds.

- *Alternative: emit from the AgentSession terminal-close path.* Rejected: jobs without an AgentSession have no session (`:1002`), so failures would be silently missed; and session transcript facts are not dispatched CloudEvents.
- *Alternative: a subscription handler reacting to terminal.* Rejected: nothing emits a CloudEvent when a job becomes terminal today — it would depend on the very emission being added here (chicken-and-egg).

### D2 — Lineage stamping and producer conformance

Build the CloudEvent with `agentid` (required) and issue/epic/workflow-run lineage when the job's launch context carries it (sourced from `State.Input` / `State.RoutedPlan`); omit only the optional business lineage when that context is absent. AgentJob submissions and routed plans without a resolved Agent identity are rejected before dispatch, so every persisted AgentJob failure can emit the required `com.mohist.agent.job.failed` event. Carry `failureReason`/`failureCategory` in the payload so the notification renderer can surface why it failed (`HermesIssueNotificationPayload` already has a `FailureReason` field).

### D3 — Self-response prevention is an envelope-only guard in `RoutingDispatchHandler`, not in the evaluator

In `RoutingDispatchHandler`, between `RoutingTableEvaluator.Evaluate` (`:57`) and `launcher.LaunchRoutedAsync` (`:118`), read `agentid` off the envelope (`CloudEventLineage`); if a rule's `AgentId` is non-empty and equals the envelope `agentid`, skip the launch and emit a structured log. Rules with empty `AgentId` and rules pointing at a different agent are unaffected — the event routes with the same standing as any other.

- *Alternative: bake the guard into `RoutingTableEvaluator`.* Rejected: the evaluator is a pure match-expression engine over `rule.Match`; the self-guard is a policy on the (rule, envelope) pair, not part of the expression. Keeping it in the handler preserves evaluator purity and co-locates the guard with the launch decision and its logging.

### D4 — Notification kind mirrors `WorkflowFailed` end-to-end

Add `NotificationKinds.AgentResponseFailed` + its `IsDefined` arm; default-on in `InboxSubscriptionState`; default-on in `HermesNotificationOptions.EnabledTypes`; add the event type to the pipe-separated subscriptions and the resolve mappings in `InboxProjectionHandler` and `HermesIssueNotificationHandler`; add a renderer branch in `HermesIssueNotificationRenderer`. `WorkflowFailed` is the closest analog; reusing its shape minimizes review surface and risk.

Inbox projection continues to no-op for events that resolve no issue context (consistent with existing behavior), so contextless job failures produce no inbox item but the event still exists for routing.

### D5 — `decidedBy` is the comment `author` model, copied through the full path

Add `DecidedBy` to `ApprovalStatus` (`StageRun`); `WorkflowRun.Approve/Reject` take an operator argument and stamp it; `StageApprovalResolved` carries `decidedBy`; `WorkflowGrain.ApproveAsync/RequestChangesAsync` take and forward the operator; `ApprovalStatusView` and its mapper expose it; CLI `BuildApprove`/`BuildReject` gain `--author`; the run-scoped and issue-scoped approve/reject HTTP DTOs accept `author`. Validation is identical to comment author: required, `.Trim()`, reject blank or >100 chars.

- *Alternative: authenticated identity from the caller.* Rejected by the issue — declaration model, same as comment author; agents cannot authenticate, and human/agent must be indistinguishable in shape.

### D6 — Breaking accept change; historical reads stay compatible

`mo run approve`/`mo run reject` and their HTTP endpoints now require an author — a **BREAKING** change for any caller that omits it. Historical approval rows carry no `decidedBy`; the read model surfaces it as empty (nullable), so legacy histories read back without error and no data migration is needed.

## Risks / Trade-offs

- [Grain-state save and event append are not atomic] -> Idempotent append by `(source, id)` plus recovery-reminder retry; a duplicate on replay is deduped by the store. Worst case the terminal state persists before the event, and the reminder emits it on the next tick.
- [Agentless failed jobs could let the reminder self-clean before the event is appended] -> D1 widens the reminder cleanup condition so the failure-event obligation keeps the reminder alive independently until emitted.
- [Self-response guard blocks only A→A, not A→B→A loops] -> Accepted Non-Goal; mitigated by a documentation warning and dry-run/routing visibility. No silent data corruption — at worst noise, which the owner sees.
- [BREAKING approve/reject displaces existing callers] -> Active development, no version compat (AGENTS.md). The supervisor preset already declares `--author` on approve/reject (`presets/supervisor/instructions.md:24`), so it keeps working once the CLI accepts the flag; the only doc fix is the generic `skill-data/mohist/SKILL.md` command reference, which omits the now-required `--author`. No external third-party callers are expected.
- [Notification spam on a flapping agent] -> Out of scope (no cooldown by design); the owner disabling the kind is the escape hatch, and the default-on choice is intentional so silence is never mistaken for "handled".

## Migration Plan

Single coordinated deploy (no version compatibility to preserve):

1. Ship server changes (failure event + lineage + self-response guard + notification kind + approval `decidedBy` plumbing) together with the CLI `--author` support and the generic `skill-data/mohist/SKILL.md` command-reference update. The supervisor preset already passes `--author` (`presets/supervisor/instructions.md:24`) and needs no text change — it has been expecting this flag.
2. No schema migration: the new event type is additive; approval `decidedBy` is nullable so existing rows read as empty.
3. Rollback is a plain revert — historical approval reads remain compatible, and any already-emitted `agent.job.failed` events are harmless (they project into an inbox kind that simply stops existing on rollback; clean up those inbox rows if desired, or leave them).

## Open Questions

- Exact payload field names for the failure event (`failureReason`/`failureCategory` vs. camel-cased variants): follow the existing `HermesIssueNotificationPayload.FailureReason` naming to avoid a second mapping. No product-level unknowns remain — `design/event-response.md` is definitive for both features.
