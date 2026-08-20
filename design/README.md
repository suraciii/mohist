# Design

`design/` targets developers and agents. It explains why architecture boundaries exist and records
the contracts that implementations must preserve. It covers domain decomposition, workflow
mechanics, and cross-module design conventions. It is not a tour of the current code. User-facing
documents live in
[`../docs/`](../docs/).

The design-spec writing rules live in [`agents.md`](agents.md). Read them before writing or
changing a document in `design/`.

## Foundational

- [agents.md](agents.md) — Design-document writing rules for agents; read before writing a spec in design/.
- [../CONTEXT.md](../CONTEXT.md) — Cross-context unified language; single entry point for term definitions.
- [architecture.md](architecture.md) — Runtime boundaries, control-plane/execution-plane responsibilities, placement rules.
- [domain-analysis.md](domain-analysis.md) — Domain analysis and context mapping: subdomain split, bounded-context relations, dependency invariants.
- [conventions.md](conventions.md) — Naming, layering, variable conventions, certainty vocabulary (facts, claims, settlement).
- [cli.md](cli.md) — Command language for humans and agents: domain ownership, progressive help / Skill context, field-selection output, error and reliability contract.
- [testing.md](testing.md) — Executable Product and Design Specs; ownership-based L0 and L1 placement; hermetic Resources, shift-left feedback, shared local and CI commands, duration budgets, and application-level gates. Gate run-directory, DAG, and lane internals live in [`../scripts/test-duration/README.md`](../scripts/test-duration/README.md).
- [observability.md](observability.md) — Observability signal split, resource budget, degradation rules, high-frequency path cost constraints.
- [eventbus.md](eventbus.md) — Event bus: CloudEvent subscription contract + single dispatcher reliable at-least-once notification.
- [event-protocol.md](event-protocol.md) — Event protocol: three-axis envelope model, business lineage stamping matrix, match expressions (CEL subset), conformance.

## Agent and execution

- [agent-execution.md](agent-execution.md) — Action, Inline Agent, Mohist Agent, AgentJob, SessionInput, AgentTurn, AgentSession, Runtime Session: layering, lifecycle ownership, activity and transcript DSL.
- [agent-api.md](agent-api.md) - Versioned direct API for PAT-authenticated external callers: public state, retry identity, event resume, and disclosure boundaries.
- [subagents.md](subagents.md) — Subagents and session trees: child launch under flat Agent, capability snapshot, parent-child link, terminal callback, cascade stop, and detach.
- [scheduled-input.md](scheduled-input.md) — Scheduled input (**WIP**): durable intent, recovery wake-ups, and ordinary follow-up delivery are implemented; due delivery still waits for another path to restore a definitely missing Runtime binding instead of initiating confirmed-missing recovery.
- [slack.md](slack.md) — Slack integration component boundary: why the adapter is standalone and stateless, Session boundary trade-offs, reliability contract, implementation order; product behavior in `docs/slack.md`.
- [event-routing.md](event-routing.md) — Agent event routing: project-scoped ordered routing table, expression matching + first-match/continue agent launch, replacing subscription priority arbitration.
- [agent-supervision.md](agent-supervision.md) — Agent supervision presets: one command installs a supervisor agent and approval/failure routing rules; escalation via all-notifications-on + `[supervisor]` comment discipline, no escalate command or system-level rate limiting.
- [agent-mentions.md](agent-mentions.md) — Comment mentions: `@` an agent name in an issue comment to launch it; third trigger path, zero config, mention is the routing decision.
- [event-response.md](event-response.md) — Agent event response: response contract (at most once, current-state based, no serialization, visible failure, no self-response) and attribution (comment author, approval decidedBy).
- [issue-watch.md](issue-watch.md) — Issue watch: issue-level autopilot switch; watching/muted declarations, fixed event set, division of labor with the routing table.

## Runtime integration

- [runtimes/](runtimes/README.md) — External execution backends: process, SDK, physical session, event and compatibility boundaries; currently OpenCode and Pi.

## Workflow core domain

- [workflow/definition.md](workflow/definition.md) — Workflow Definition DSL: semantic model (Expect as a first-class concept), single authoritative validator (rule catalog, three entry points incl. `mo` local validation), implementation-side semantic index; syntax authority in [`docs/workflow-definition.md`](../docs/workflow-definition.md).
- [workflow/actions.md](workflow/actions.md) — Action plugin model: manifest contract, single input channel, structured output, capability injection, catalog validation, failure-recovery orchestration.
- [workflow/builtin-workflows.md](workflow/builtin-workflows.md) — Design points of built-in workflows (local / github-pr); the yaml definitions are the source of truth.
- [workflow/profile.md](workflow/profile.md) — Workflow Profile: Project-scoped collection, default selection, Issue override, Run snapshot.
- [workflow/run-state.md](workflow/run-state.md) — WorkflowRun State: persisted content boundary, read/write cost, and one-way format migration rules at startup.
- [workflow/variables.md](workflow/variables.md) — Workflow Variables: Project / Issue / Run resources, merging, live effect, `setVars` semantics.
- [workflow/task-dispatch.md](workflow/task-dispatch.md) — Single authority for `with` / `expect` template evaluation timing: Server dispatch carries the original declaration and an immutable attempt snapshot; Runner renders once at the execution entry before calling the Action.
- [workflow/recovery.md](workflow/recovery.md) — Failure recovery: recovery declarations, when matching, runner-built recovery tasks.
- [workflow/issue-coordination.md](workflow/issue-coordination.md) — Cross-aggregate interaction of Issue, WorkflowRun, Runner, Session.

## Supporting topics

- [auth.md](auth.md) — Auth and identity: single admin plus service/agent principals, file and signed credentials, device authorization login, Runner machine credentials, Scope enforcement, and attribution.
- [repositories.md](repositories.md) — Repository execution: Project resource authority, Issue binding, live dispatch resolution (**WIP**).
- [workspace.md](workspace.md) — Workspace (**WIP**): first-class persistent execution environment under a Project, with Origin resolution, named Runner materialization, binding affinity, archival, and reclamation; Workflow cross-Runner rematerialization and Slack channel-archive propagation remain gaps.
- [hermes-webhook.md](hermes-webhook.md) — Hermes notification gateway: event types, payload, signature, delivery reliability.
- [outbound-webhook.md](outbound-webhook.md) — Outbound webhook: implemented v1 general HTTP delivery with CloudEvents, event selection, configurable authentication, 2xx success, and failure inspection; retries, redelivery, attempt history, and Web management remain later capabilities.
- [github-integration.md](github-integration.md) — GitHub integration: signed ingress, feed/close translation, and write-back are implemented; PR branch correlation, App identity, and failure inspection remain gaps; product behavior in [`docs/github.md`](../docs/github.md).
- [issue-breakdown.md](issue-breakdown.md) — Composite Issue / sub-issue design: implemented parent-child model, status aggregation, composite advancement, and isolation constraints from Epic; multi-repo resources in `docs/repositories.md`.
- [issue-templates.md](issue-templates.md) — Body structure and design rationale of the three issue templates (Feature / Bug / Refactor).
- [prompt-management.md](prompt-management.md) — Project-scoped Prompt (**WIP**), builtin fallback, Workflow key reference.
- [runner.md](runner.md) — Runner and scheduling: each owner is its own dispatch ledger (no second copy, no reconcile), pull-only claim / poll / report, presence and runner-lost closeout, stop settles by identity.
- [runner-transport.md](runner-transport.md) — SignalR-to-WebSocket Runner control migration: preserved HTTP dispatch, JSON-RPC 2.0 methods, and cutover order.
- [task-log.md](task-log.md) — Task execution log collection pipeline, report channel, storage ownership, settlement-recorded terminal ownership.
- [db-migrations.md](db-migrations.md) — EF Core migration authoring contract and the squash procedure: baseline, squash floor, history remap, equivalence verification.
- [issue-list-read.md](issue-list-read.md) — Low-bandwidth issue-list reading and request isolation: list summary model, event invalidation, cold transport.
- [web-ui.md](web-ui.md) — Web UI design boundary.
- [session-timeline.md](session-timeline.md) — AgentSession timeline presentation model: transcript-fact-derived activity entries, phrasing and salience discipline, Mohist domain action recognition, raw view.

## Decision records

- [decisions/issue-owns-epic-membership.md](decisions/issue-owns-epic-membership.md) — Issue holds the current Epic membership; Project-scoped number identity and cross-aggregate recovery flow.
- [decisions/epic-status-revival.md](decisions/epic-status-revival.md) — Epic `done` auto-revival and `closed` link rejection.
- [decisions/mobile-pwa.md](decisions/mobile-pwa.md) — Mobile Web UI as an installable PWA: decision record for a proposal that is not implemented.
- [decisions/squashed-baseline.md](decisions/squashed-baseline.md) — Point-in-time record of the accepted schema deltas at the current migration squash baseline.
- [decisions/workflow-run-profile-naming.md](decisions/workflow-run-profile-naming.md) — Why Run Variables retain the historical WorkflowRunProfile persistence name.
