# Design

`design/` targets developers and agents. It explains why architecture boundaries exist and records
the contracts that implementations must preserve. It covers domain decomposition, workflow
mechanics, and cross-module design conventions. It is not a tour of the current code. User-facing
documents live in
[`../docs/`](../docs/).

Write all active design prose in English. Use short sentences, active voice, American spelling, and stable
terms. Use ASD-STE100 writing rules as a target. Do not claim compliance. Keep domain identifiers, field
names, API names, commands, serialized values, and code symbols as-is when their exact spelling is part of
the contract. Use `must`, `may`, and `must not` for requirements, options, and prohibitions.

## Writing a design spec

A design spec is the authoritative statement of why the system is divided this way and how its
parts must preserve target behavior. People, agents, and implementations must read the same model
from it.
Do not let agents guess rules. Do not let the current code decide for the target design.

### Explain the design drivers

- Start with the problem that requires a design decision. State why the existing or obvious model
  is insufficient.
- Name the forces that shape the solution: ownership, lifecycle, consistency, reliability,
  security, cost, or operability.
- Explain why the chosen boundary satisfies those forces and which trade-off it accepts.
- Record rejected alternatives only when they could reasonably return in a later change. State the
  reason for rejection, not the history of the discussion.
- Describe the macro structure before fields, endpoints, algorithms, or persistence. A reader must
  understand the dependency direction before implementation detail appears.
- Keep exact mechanics only when they form a durable contract or remove a real ambiguity. Do not
  translate a method body, call chain, database procedure, or source tree into prose.

### Define the model first

- After the design drivers, write what the concept is and what it is not.
- State who owns it, where it applies, how to identify it, when it is created or ends, and what must always hold.
- Introduce only concepts with business meaning. Do not add new nouns without an identity, behavior, or rules of their own.
- Keep only the fields the current behavior needs. Do not add resources, scopes, or APIs ahead of possible future capabilities.
- Do not invent a shared domain concept just because several data shapes look alike.
- Do not treat read order, storage layout, or call chains as the domain model.
- Mention providers, resolvers, or managers only to explain code boundaries. Do not use them as domain nouns.
- Let one noun mean one thing. Rename or split immediately when names collide or become ambiguous.
- Separate resources with different owners, scopes, or lifecycles. Do not bind them together with a generic `config`.
- Define a rule in exactly one document. Other documents link to it; they do not copy it.

### State the semantics

- Write definite rules. Do not state only design intent.
- Connect each important rule to the design force it protects. Do not record the whole discussion.
- Write the full order. State who comes first, who comes after, and who overrides whom.
- Write the resolution timing. State what takes effect live and what is fixed at startup.
- Write the write target. State which resource one operation modifies and which it does not.
- Write failure behavior. Reject invalid states; do not swallow errors silently.
- Use pseudocode only when the algorithm itself is part of the contract. It must remove ambiguity
  without mirroring a current method body or call chain.
- Express merging, fallback, selection, and state changes with inputs and outputs.
- Use the same interface for the same semantics. Do not duplicate APIs for different callers.
- Write caller restrictions as parameter restrictions. Do not wrap them into a new domain capability.
- Write behavior first; then write how YAML, JSON, API DTOs, or the database express it.
- Let schema and validators decide whether a DSL is valid. Do not let the LLM guess.

### Choose the right expression

- Prefer short sentences. One sentence states one rule.
- Prefer domain nouns and product nouns. Use technical nouns only in implementation design.
- Use canonical names. Keep casing, singular/plural, and field paths consistent.
- Every plain-text fence must choose exactly one semantic marker: `text diagram` or `text literal`.
- Use `text diagram` only when an ASCII diagram makes a boundary, ownership relation, dependency,
  sequence, hierarchy, or state transition easier to understand. Do not draw when prose is already clear.
- Use `text literal` for command output, syntax, protocols, pseudocode, data shapes, and other
  preformatted text that is not a diagram. Bare `text` fences are invalid.
- Use only ASCII characters in diagrams. Do not add PlantUML, Mermaid, Unicode line art, or Unicode arrows.
- Do not use raw HTML. Markdown is the only document markup.
- Draw only real concepts. Give every arrow a meaning.
- Write key rules in prose. Do not make a diagram the only source of truth.
- Use pseudocode for definite computations.
- Use minimal input/output examples when ambiguity must be resolved.
- Make examples behave like tests. Keep only examples that distinguish between readings.
- Ensure YAML, JSON, command, and API examples parse or run as written.

### Use the minimal structure

Start from the structure below. Delete sections that have no content. Do not add empty sections for symmetry.

```text literal
# Name

The problem and why a design decision is necessary.

## Design Drivers
Constraints, forces, chosen trade-offs, and rejected alternatives that may recur.

## Model
Resources, ownership, references, and the minimal data shape.

## Semantics
Selection, merging, state changes, timing, errors, and interfaces.

## Examples
A small number of inputs and expected outputs.

## Status
Open questions and current implementation gaps.
```

Put API, Writes, Merge, and similar topics in `Semantics` subsections. Split them into standalone sections only when they are complex enough.

### Before committing

- Confirm the reader can answer: what problem does this solve, why is this boundary here, and which
  trade-off does it accept?
- Confirm the reader can answer: what is it? who owns it? what is the scope?
- Confirm the reader can answer: how is it selected? how is it read? how is it modified?
- Confirm the reader can answer: who overrides whom on conflict? when does it take effect?
- Confirm the reader can answer: what happens on failure? which states are not allowed?
- Confirm the prose describes the target design. Move current implementation gaps to `Status`.
- Delete duplicate rules, behavior-less abstractions, and prose that only explains code steps,
  method bodies, storage operations, or call chains.
- Check that diagrams, pseudocode, examples, and prose express the same semantics.
- Have another agent read the spec read-only. If it still needs the code to implement, complete the spec.
- Have two independent agents derive behavior from the spec. Remove ambiguity when they disagree.

## Foundational

- [agents.md](agents.md) — Design-document writing rules for agents; read before writing a spec in design/.
- [../CONTEXT.md](../CONTEXT.md) — Cross-context unified language; single entry point for term definitions.
- [architecture.md](architecture.md) — Runtime boundaries, control-plane/execution-plane responsibilities, placement rules.
- [domain-analysis.md](domain-analysis.md) — Domain analysis and context mapping: subdomain split, bounded-context relations, dependency invariants.
- [conventions.md](conventions.md) — Naming, layering, variable conventions.
- [cli.md](cli.md) — Command language for humans and agents: domain ownership, progressive help / Skill context, field-selection output, error and reliability contract.
- [testing.md](testing.md) — Two test tracks (spec/unit), external dependencies, time dependencies, fake entry points.
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
- [runner.md](runner.md) — Runner and scheduling: each owner is its own dispatch ledger (no second copy, no reconcile), pull-only claim / poll / report, presence and runner-lost closeout.
- [task-log.md](task-log.md) — Task execution log collection pipeline, report channel, storage ownership.
- [issue-list-read.md](issue-list-read.md) — Low-bandwidth issue-list reading and request isolation: list summary model, event invalidation, cold transport.
- [web-ui.md](web-ui.md) — Web UI design boundary.
- [session-timeline.md](session-timeline.md) — AgentSession timeline presentation model: transcript-fact-derived activity entries, phrasing and salience discipline, Mohist domain action recognition, raw view.

## Decision records

- [decisions/issue-owns-epic-membership.md](decisions/issue-owns-epic-membership.md) — Issue holds the current Epic membership; Project-scoped number identity and cross-aggregate recovery flow (issue-412).
- [decisions/epic-status-revival.md](decisions/epic-status-revival.md) — Epic `done` auto-revival and `closed` link rejection (issue-392).
