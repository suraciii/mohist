# Design

`design/` targets developers and agents. It records architecture boundaries, domain decomposition, workflow mechanics, and cross-module design conventions. User-facing documents live in [`../docs/`](../docs/).

Write new or rewritten design prose in English; keep domain identifiers, field names, API names, and code symbols as-is. Existing Chinese documents converge to English as they are revised, so language migration never mixes with unrelated design changes.

## Writing a design spec

A design spec is the authoritative statement of how the system implements target behavior. People, agents, and implementations must read the same model from it.
Do not let agents guess rules. Do not let the current code decide for the target design.

### Define the model first

- Write what the concept is first. Then write what it is not.
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
- Record only design reasons that affect later changes. Do not record the whole discussion.
- Write the full order. State who comes first, who comes after, and who overrides whom.
- Write the resolution timing. State what takes effect live and what is fixed at startup.
- Write the write target. State which resource one operation modifies and which it does not.
- Write failure behavior. Reject invalid states; do not swallow errors silently.
- Use pseudocode for definite algorithms. A reader must be able to implement them step by step.
- Express merging, fallback, selection, and state changes with inputs and outputs.
- Use the same interface for the same semantics. Do not duplicate APIs for different callers.
- Write caller restrictions as parameter restrictions. Do not wrap them into a new domain capability.
- Write behavior first; then write how YAML, JSON, API DTOs, or the database express it.
- Let schema and validators decide whether a DSL is valid. Do not let the LLM guess.

### Choose the right expression

- Prefer short sentences. One sentence states one rule.
- Prefer domain nouns and product nouns. Use technical nouns only in implementation design.
- Use canonical names. Keep casing, singular/plural, and field paths consistent.
- Use PlantUML when prose cannot express a relationship or flow clearly. Do not draw when prose is already clear.
- Draw only real concepts. Give every arrow a meaning.
- Write key rules in prose. Do not make a diagram the only source of truth.
- Use pseudocode for definite computations.
- Use minimal input/output examples when ambiguity must be resolved.
- Make examples behave like tests. Keep only examples that distinguish between readings.
- Ensure YAML, JSON, command, and API examples parse or run as written.

### Use the minimal structure

Start from the structure below. Delete sections that have no content. Do not add empty sections for symmetry.

```text
# Name

One-sentence definition.
One-sentence boundary.

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

- Confirm the reader can answer: what is it? who owns it? what is the scope?
- Confirm the reader can answer: how is it selected? how is it read? how is it modified?
- Confirm the reader can answer: who overrides whom on conflict? when does it take effect?
- Confirm the reader can answer: what happens on failure? which states are not allowed?
- Confirm the prose describes the target design. Move current implementation gaps to `Status`.
- Delete duplicate rules, behavior-less abstractions, and prose that only explains code steps.
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
- [agent-api.md](agent-api.md) — Agent call boundary shared by Web, CLI, and external integrations: unified capabilities, state, identity, reliability decisions.
- [subagents.md](subagents.md) — Subagents and session trees: child launch under flat Agent, capability snapshot, parent-child link, terminal callback, cascade stop, timed input.
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

- [auth.md](auth.md) — Auth and identity (**finalized, pending implementation**): single admin plus service/agent principals, file-based and signed credentials, device authorization login, Runner machine credentials, attribution.
- [repositories.md](repositories.md) — Repository execution: Project resource authority, Issue binding, live dispatch resolution (**WIP**).
- [workspace.md](workspace.md) — Workspace: first-class persistent execution environment under a Project (**WIP**): Origin unique resolution, dynamic creation, binding and scheduling affinity, archival and runner directory reclamation.
- [hermes-webhook.md](hermes-webhook.md) — Hermes notification gateway: event types, payload, signature, delivery reliability.
- [outbound-webhook.md](outbound-webhook.md) — Outbound webhook (**WIP**): OHS + PL, CloudEvent as publication language, expression subscription + HMAC signing + best-effort delivery.
- [github-integration.md](github-integration.md) — GitHub integration (**WIP**): inbound event reception and signature verification, feed/close/approval translators, write-back, credential boundaries; product behavior in [`docs/github.md`](../docs/github.md).
- [issue-breakdown.md](issue-breakdown.md) — Composite Issue / sub-issue design (**finalized, pending implementation**): parent-child model, status aggregation, composite advancement, isolation constraints from Epic; multi-repo resources in `docs/repositories.md`.
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
