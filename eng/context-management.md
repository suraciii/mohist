# Context Management

This document specifies how the Mohist repository manages context: what the
repository stores, what it must not store, and where each kind of information
lives. It governs every file whose purpose is to inform future readers, humans
and agents, rather than to execute.

This is an engineering practice of this repository, not a product
specification. Product and design specifications describe Mohist; `eng/`
specifies how the repository itself is built, tested, and documented.

## Design Drivers

Context management serves the agent. The repository is the primary context
source for every agent session, and the goal of this system is that an agent
loads exactly the context its work needs: every necessary fact at a known
location, and no stale or irrelevant material in the way. Human developers
need largely the same effective context, so what serves the agent serves both.

Two failure modes work against this goal:

- **Missing durable context.** Rules, terms, and decisions that exist only in
  chat history, closed Issues, or merged pull requests are re-derived
  inconsistently, or lost. The agent cannot load a fact it needs.
- **Accumulated transient context.** Plans, research dumps, and progress logs
  describe a moment. When the moment passes, nothing maintains or deletes
  them. The agent loads stale facts that still look authoritative.

Both failures reduce the same measure: the share of the loaded context that is
true, current, and relevant.

## Principles

**The repository holds durable context only.** Durable context stays true
indefinitely and changes only when the product or the design changes: the
product specification, the design specification, the glossary, standing rules,
and decision records.

**Transient context never enters the repository.** Transient context describes
momentary state: plans, research notes, progress logs, review drafts, probe
output, one-off measurements, and runtime state. Its home is the Mohist Issue
and the parent workspace, which maintain and retire it as work proceeds.

**The specification is the delta.** A change to the product or the design is
expressed by editing the specification itself. The pull request diff is the
change record, and review happens on the Issue and the pull request. The
repository must not grow an in-repo proposal, change-tracking, or planning
format.

## Where context lives

One fact has one home. Other documents link to the home; they never restate
the fact.

Organize specifications by subdomain and feature, not by implementation
package. A feature is a lasting capability, not an Issue or a release.

The [Product Vision](../docs/vision.md#product-capability-areas) owns capability
areas and end-to-end user paths. [Domain Analysis](../design/domain-analysis.md)
owns business boundaries and relationships; [CONTEXT.md](../CONTEXT.md#product-organization)
defines the shared terms. Link these views rather than duplicating their
contents. Do not add a capability-area directory above subdomains or reorganize
source code merely to mirror the specification tree. Feature priorities and delivery plans
belong to Issues, Epics, and pull requests, not another repository ledger.

- **`specs/<subdomain>/<feature>/spec.md`** defines observable behavior,
  failure and unknown outcomes, safety boundaries, and acceptance scenarios.
- **`specs/<subdomain>/<feature>/design.md`** explains mechanisms and
  implementation contracts. Create it only when the feature needs a separate
  design explanation. Link to product rules instead of repeating them.
- **`specs/README.md`** is the single human-facing introduction to the spec
  layout, with a few starting links. Do not add subdomain or feature READMEs,
  duplicate the directory listing, or summarize each feature's rules here.
- **`docs/`** holds product vision, tutorials, and operating guides. Guides
  link to the owning specification instead of defining behavior again.
- **`eng/`** holds repository engineering practices, not product behavior.

Create directories only for content that exists. Do not require per-feature
glossaries, manifests, rule files, or separate acceptance documents. Keep the
existing domain map in [Domain Analysis](../design/domain-analysis.md); a new
directory layout does not require another map or a new domain model.

Cross-cutting homes:

- **Root `AGENTS.md`** holds only rules that apply across the whole
  repository. Each rule links to the document that owns the detail.
- **`CONTEXT.md`** is the single entry point for term definitions.
- **`README.md` files** introduce the project or documentation to human
  readers. They are not required in every directory. Agent instructions
  belong in `AGENTS.md`; both entry points link to the same specifications.
- **Scoped rule files are named `AGENTS.md`.** Agent tooling loads `AGENTS.md`
  files automatically when work enters their tree; a rules file that loads
  itself cannot be forgotten. Names such as `_agents.md` or `agents.md` hide
  the same content from that mechanism and must not be used.
- **`design/decisions/`** holds durable decision records; see
  [Decision records](#decision-records).
- **Code comments** hold narrow-scope technical detail. They explain why,
  never what.

## Finding and changing context

Start with the applicable `AGENTS.md` rules and locate the owning feature at
`specs/<subdomain>/<feature>/spec.md`. The human-facing README is not a required
step for agents. Read the spec before changing its behavior. Read its design and
implementation when the task requires them. Follow dependency links only for
rules the task relies on; do not load every feature in the subdomain.

Each cross-feature dependency names the fact it consumes and links to that
fact's owner. Runner owns work-confirmation sources, Session owns execution
evidence interpretation, and AgentOps owns their presentation. None of these
features duplicates another's rules.

Before editing, identify the required behavior, its owner, relevant failure
boundaries, and how the change will be verified. Resolve missing or conflicting
contracts explicitly rather than treating current code or old chat as the spec.

Change the spec when behavior changes and the design when mechanisms change.
Update affected links in the same change. Do not require a documentation diff
when no durable fact changes. Plans and verification output stay with the task.

## Specification Ownership

Feature behavior and feature-local mechanisms live together in `specs/`.
`docs/` holds the vision, conceptual reading guide, tutorials, and operations
guides. `design/` holds the domain map, cross-domain architecture and
conventions, and decision records. Database migration practices live in
[Database Migrations](database-migrations.md), not in a product feature.
Move each rule once, remove its old body, and update references together.
Do not keep compatibility copies of specifications in the guide directories.

## Decision records

A design specification explains the current boundary and the constraints it
must satisfy. Use a decision record when rejected alternatives and an accepted
trade-off must remain available to future changes. Keep that detailed rationale
in the record and link to it from the specification. Specifications and
`AGENTS.md` state the target state; they do not narrate change history.

Each record carries, in order:

- a Status line: `Status: accepted`, or `Status: superseded by <link>` to the
  record that replaced it;
- `## Problem`: the force that required a decision;
- `## Decision`: the chosen rule, in present tense;
- `## Alternatives considered`: every serious alternative and why it lost.
  This section is mandatory. A decision that does not record what it beat
  will be disputed again;
- `## Consequences`: what the trade-off cost and bought.

Rules:

- When a new record supersedes an existing one, the same change marks the old
  record's Status line. A superseded record stays readable; the Status line is
  the only structural edit it receives.
- Factual references (paths, symbols, defaults) are updated in place when the
  implementation moves. The decision itself is never rewritten.
- Keep a record while it can still guide a future change: it owns a boundary,
  prevents a recurring mistake, or states the condition for reintroducing what
  was removed. Age and length are never reasons to delete one.

## Transient context

Transient context belongs to the workflow, not the repository:

- Issue proposals, design drafts, task lists, and review records live on the
  Mohist Issue and its comments.
- Plans and research notes live in the parent workspace of the worktree.
- Runtime and agent state (`.pi/`, `.pages/`, session data, test reports)
  stays local and must be covered by `.gitignore`.

When a transient artifact is found in the repository, move its still-durable
facts into their owning document and remove the artifact. When it is unclear
whether content is durable, apply the test: a fact is durable when it must
remain true after the current work item closes.

## Writing rules

These rules govern every specification document, including `specs/`, `docs/`,
`design/`, and `eng/`.

- Write active prose in English. Use short sentences, active voice, American
  spelling, and stable terms. Use ASD-STE100 writing rules as a target; do not
  claim compliance. Keep domain identifiers, field names, API names, commands,
  serialized values, and code symbols in their exact spelling. Use `must`,
  `may`, and `must not` for requirements, options, and prohibitions.
- Write only statements an implementation can be checked against. A sentence
  that no implementation could violate is not a spec rule; delete it.
- Keep terms consistent with [`CONTEXT.md`](../CONTEXT.md). Define a term
  once and link to it.
- A document states its current gaps in its own Status or Implementation Gaps
  section. Documents and indexes do not carry lifecycle frontmatter, status
  banners, or WIP annotations; status rots outside the document that owns it.
- State normative rules in prose, and use numbered steps for a linear
  procedure.
- Commands and examples must run or parse as written, each one independently.
  The documentation gate cannot prove that a command has the documented
  effect; verify examples against the owning implementation.
- Draw a diagram when a boundary, ownership relation, dependency, sequence,
  hierarchy, or state transition is easier to understand as a picture. Author
  the diagram in Mermaid, render it to ASCII with `grok-mermaid`, and store
  the rendered art in a `text diagram` fence. Keep node and edge labels short
  so the render stays legible; give every arrow a meaning, and draw only real
  concepts. Do not draw when prose is already clear. Write key rules in prose,
  so a diagram is never the only source of truth.
- Do not commit raw `mermaid` fences; Mermaid is the authoring form, ASCII
  the storage form. Use `text literal` for command output, syntax, protocols,
  pseudocode, and data shapes. A bare `text` fence is invalid.
- Do not use raw HTML, including HTML comments. Markdown is the only document
  markup.
- Do not use tables. Give the same information as short prose or one concrete
  example.

## Verification

`npm run docs:check` gates documentation mechanics across `specs/`, `docs/`,
`design/`, and `eng/`: Latin-script prose, link targets, and diagram fences. It also gates
that every decision record carries a Status line and an `## Alternatives
considered` section. Reviewers enforce the placement and durability rules in
this document; no gate can judge them.

## Product Specification Writing

### Rules

- Write the spec before implementing. Product documents define the target
  product. Issues bring the implementation to the spec; the spec does not
  follow the implementation. A document can describe a capability before
  implementation, and its body does not need to change when delivery finishes.
- Lead with the user problem and why the product behavior exists. Explain the
  constraint or trade-off that makes the rule necessary before listing the
  rule itself. Remove generic motivation, introductory padding, and common
  knowledge.
- One section, one purpose. A heading states the question that its section
  answers. Prefer a list to a paragraph.
- Explain the product, not the code. Conceptual guidance uses product and
  domain language to explain mental models, ownership boundaries, and visible
  behavior. Do not turn classes, methods, handlers, source call chains, or
  storage steps into prose. Formal CLI, DSL, and API contracts can keep the
  exact commands, syntax, and fields that users must use, in task guides and
  reference sections. A single `Implementation source:` footer can point to
  implementation entry points.
- The body is the spec. If the implementation differs materially from a
  document, add an Implementation Gaps section that states the current state
  as a plain product fact. Never put divergence in a status list or delivery
  ledger, and do not reduce the body to a current-feature list.
- Check gaps before changing facts. Before you change a factual statement,
  check whether the document's Implementation Gaps section already records the
  difference. Do not change a target spec back to current behavior.
- WIP product ideas use future-state language and record their current state
  in an Implementation Gaps section. After the requirements and spec are
  final, move the document to its product area.

### Minimal structure

A product spec states what the product must satisfy; the implementation
aligns to it. Start from the structure below. Delete sections that have no
content. Do not add empty sections for symmetry.

```text literal
# Name

Opening paragraph: what this is and why the product behavior exists, in two
to four sentences. It lets the reader decide whether this is the document
they need.

## Product Commitments
What a user or Agent can rely on, stated as promises.

## <Concept>
One section per user-visible concept: what it is and is not, when it is
created and ends, who owns it.

## <Capability>
One section per user-facing capability: how to use it and the rules that
hold. Exact CLI, DSL, and API syntax lives here.

## Boundary
What the product deliberately does not do. Optional.

## Implementation Gaps
Where the implementation does not yet match this document, stated as plain
product facts.
```

## Design Specification Writing

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
- When rejected alternatives could reasonably return in a later change, link to a decision record
  that owns their rationale. Do not copy that record into the design spec.
- Describe the macro structure before fields, endpoints, algorithms, or persistence. A reader must
  understand the dependency direction before implementation detail appears.
- Keep exact mechanics only when they form a durable contract or remove a real ambiguity. Do not
  translate a method body, call chain, database procedure, or source tree into prose. Mention a
  code symbol only when it names a durable implementation boundary or links a current gap to its
  source.

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

- State one rule per sentence.
- Prefer domain nouns and product nouns. Use technical nouns only in implementation design, and
  define terms a new reader may not know.
- Use canonical names. Keep casing, singular/plural, and field paths consistent.
- Use pseudocode for definite computations.
- Use minimal input/output examples when ambiguity must be resolved.
- Make examples behave like tests. Keep only examples that distinguish between readings.

### Use the minimal structure

Two kinds of design specs exist. Start from the matching structure below.
Delete sections that have no content. Do not add empty sections for symmetry.

A **concept spec** defines one concept: one resource, mechanism, or contract.

```text literal
# Name

The problem and why a design decision is necessary.

## Design Drivers
Current constraints and forces; links to relevant decision records.

## Model
Resources, ownership, references, and the minimal data shape.

## Semantics
Selection, merging, state changes, timing, errors, and interfaces.

## Examples
A small number of inputs and expected outputs. Optional.

## Status
Open questions and current implementation gaps.
```

Put API, Writes, Merge, and similar topics in `Semantics` subsections. Split them into standalone sections only when they are complex enough.

A **subsystem spec** defines one subsystem's boundaries and the decisions
that must remain true.

```text literal
# Name

The subsystem's boundary and what this document records.

## Core Decisions
The load-bearing choices, one line each; the rules live in the sections
below. Include this register only when the subsystem carries many decisions.

## System Boundary
Components, the boundary diagram, and what each component owns and does not
own.

## <Concern>
One section per design concern, named by the question it answers: the model,
the rules, and the failure behavior.

## Non-Goals
Scope exclusions.

## Status
Current implementation state and gaps.
```

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
