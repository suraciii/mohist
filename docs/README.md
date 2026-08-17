# Mohist Documentation

This documentation is for **users**. It provides a reading path organized by
product area. Architecture and domain analysis are under
[`../design/`](../design/).

If you are new to Mohist, read the [repository README](../README.md) first.

## Part 1: Start

- [Product Vision](vision.md): Where Mohist is going and how independent Agents
  work with external interaction locations
- [Getting Started](getting-started.md): Start from zero and move one Issue
  through the complete Workflow with a Mohist Agent, External Agent, or `mo`
- [Core Concepts](concepts.md): Understand the Mohist production-line model
- [Agents and AgentSessions](agent-sessions.md): Configure and use a Mohist
  Agent directly, and understand the work and session relationship

## Part 2: Workflows

- [The Workflow](the-workflow.md): What happens in Draft, Plan, Build, Check,
  and Integrate
- [Workflow Profile](workflow-profiles.md): Configure stages, tasks, checks, and
  Approval policy
- [Workflow Definition Reference](workflow-definition.md): The complete syntax
  for stages, tasks, expectations, recovery, and template expressions

## Part 3: Work Management

- [Repositories](repositories.md): Declare multiple repositories as Project
  execution resources and route each Issue to its target repository
- [Workspace](workspaces.md): Use persistent execution environments across
  sessions and Agents, with clean Issue initialization and persistent reuse for
  a Slack channel
- [Issue Management](issues.md): Create, start, approve, recover, and close
  Issues
- [Composite Issues and Sub-issues](sub-issues.md): Track a cross-repository
  requirement in one Issue and move its sub-issues through separate Workflows
- [Planning with Epics](epics.md): Organize separate Issues into a product goal
  that can advance automatically

## Part 4: Observation and Operations

- [Web UI Guide](web-ui.md): The board, details, evidence, and settings in the
  fallback operations and visualization plane
- [CLI Reference](cli-reference.md): The `mo` command language, command map, and
  interaction contract shared by External Agents and people
- [Observability](observability.md): Detect runtime anomalies safely and retain
  enough information for diagnosis

## Part 5: Execution Backends and Extensions

- [Action Contracts](actions/README.md): Workflow Action inputs, outputs, and
  behavior, including `mohist/opencode` and `mohist/pi`
- [External Agent API](agent-api.md): Call the shipped private API to delegate
  Agent work, recover keyed writes, read public state, and resume Session events
- [Runner Guide](runner.md): Run the execution plane and configure concurrency
- [Skills](skills.md): Give reusable capabilities to Mohist Agents and External
  Agents
- [Slack](slack.md): Use the Mohist App to manage connections conversationally,
  and use each Agent App as an independent bot in direct messages and channels
- [GitHub](github.md): Use GitHub as a requirement entry point, progress board,
  and Approval source through labels, reviews, and progress updates
- [Agent Event Routing](event-routing.md): Subscribe to events from any entity
  with a Project routing expression, then trigger Mohist Agent responses in
  order
- [Agent Supervision](agent-supervision.md): Install a supervision Agent with
  one command. It approves work and repairs failures for you until it stops and
  asks you to act.
- [Subagents and Session Trees](subagents.md): Let an Agent decompose work in its
  session through child-session spawn, terminal reports, cascading stop, and
  scheduled input

## Part 6: Deployment and Operations

- [Self-hosting](self-host.md): Run Mohist continuously on a NAS, home server,
  or laptop
- [Authentication and Access](auth.md): One Administrator plus machine
  Principals, with local zero-login access, CLI device authorization, script
  tokens, Runner registration, and Agent attribution
- [Hermes Notifications](hermes-notifications.md): Push approval points,
  failures, and completion to your chat tool
- [Troubleshooting](troubleshooting.md): Handle failures, blocked state, and
  drift

## Part 7: Product Proposals (WIP)

> These proposals have aligned requirements but are not implemented. **These
> features do not currently exist.** Move them to the appropriate part above
> after implementation.

- [Mobile PWA and Push](mobile-pwa.md): A deferred proposal for viewing progress
  and receiving notifications on a phone

## Writing Contract

- **Agent-facing writing rules**: Read and follow
  [_agents.md](_agents.md) before you edit `docs/`.
- **One purpose per section**: A heading states the question that its section
  answers. Start a concept with the user problem and the reason for the chosen
  product behavior. Keep only rationale that explains a constraint or
  trade-off; remove generic motivation, introductory padding, and common
  knowledge. Prefer a list to a paragraph and a table to a list.
- **Explain the product, not the code**: Describe the user's mental model,
  ownership boundary, and visible behavior. Do not narrate classes, methods,
  handlers, storage steps, or source control flow. Put exact commands and
  fields in task guides and reference sections only when readers must use them.
- **One authority for each rule**: Define a rule in one document. Other
  documents must link to it and must not copy it.
- **Spec before implementation**: Product documents define the target product.
  Issues bring the implementation to the spec; the spec does not follow the
  implementation. A document can describe a capability before implementation.
  Its Issue delivers the capability, and the body does not need to change when
  delivery finishes.
- **Separate implementation gaps**: If the implementation differs materially
  from a document, add an Implementation Gaps section that states the current
  state and corresponding Issue. Do not reduce the body to a current-feature
  list. The body is the spec; the gap is a footnote.
- **Self-contained commands**: Agents read and run these documents. Every shell
  and CLI example must run independently as written. It must not depend on an
  instruction to replace a value shown earlier.
- **Check gaps before changing facts**: Before you change a factual statement,
  check whether the document's Implementation Gaps section already records the
  difference. Do not change a target spec back to current behavior.
- **WIP product proposals**: Put product ideas with unresolved requirements in
  Part 7. Add `status: wip-not-implemented` frontmatter and use future-state
  language. After the requirements and spec are final, move the document to its
  product area and remove the WIP marker.
- **Language**: Write active prose in English. Preserve the exact spelling of
  product terms, configuration fields, commands, identifiers, and code symbols.
  Use short sentences, active voice, and American spelling. Treat ASD-STE100 as
  a writing target, not a compliance claim.
- **Classify text fences**: Use `text diagram` for an ASCII visualization of a
  boundary, ownership relation, dependency, sequence, hierarchy, or state
  transition. Use `text literal` for command output, syntax, protocol examples,
  pseudocode, or user text. Do not use a bare `text` fence. Define normative
  rules in prose, use a table for exact mappings, and use numbered steps for a
  linear procedure.
- **Use technical detail only where users need it**: Conceptual and product
  guidance must explain mental models and visible behavior without endpoints,
  fields, component classes, source paths, or source call chains. Formal CLI,
  DSL, and API contracts can preserve the exact commands, syntax, and fields
  that users must use. Never narrate implementation classes or source control
  flow. A single `Implementation source:` footer can point to implementation
  entry points.
- **Markdown only**: Do not use raw HTML, including HTML comments, in active
  documentation.
- **Know the automated boundary**: `npm run docs:check` enforces Latin-script
  prose, Markdown-only structure, text-fence classification, ASCII diagrams,
  and local links. It cannot prove that prose is English or that a command has
  the documented effect. Verify CLI examples against current help and focused
  command tests, and verify behavioral claims against the owning implementation.
- **Consistent terms**: Use Project, Issue, Workflow, Epic, Inline Agent, Mohist
  Agent, Agent Connection, AgentSession, and Skill consistently.

Open an Issue when you find an outdated statement.
