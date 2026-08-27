# Context Management

This document specifies how the Mohist repository manages context: what the
repository stores, what it must not store, and where each kind of information
lives. It governs every file whose purpose is to inform future readers, humans
and agents, rather than to execute.

This is an engineering practice of this repository, not a product
specification. `docs/` and `design/` specify the Mohist product; `eng/`
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

- **Root `AGENTS.md`** holds only rules that apply across the whole
  repository. Each rule links to the document that owns the detail.
- **`CONTEXT.md`** is the single entry point for term definitions.
- **`docs/`** holds the product specification: what the product must satisfy.
- **`design/`** holds the design specification: why the boundaries exist and
  which contracts implementations must preserve.
- **`eng/`** holds repository engineering practices, such as this document.
  They govern the repository, not the product.
- **Scoped rule files are named `AGENTS.md`.** Agent tooling loads `AGENTS.md`
  files automatically when work enters their tree; a rules file that loads
  itself cannot be forgotten. Names such as `_agents.md` or `agents.md` hide
  the same content from that mechanism and must not be used.
- **`design/decisions/`** holds durable decision records; see
  [Decision records](#decision-records).
- **Code comments** hold narrow-scope technical detail. They explain why,
  never what.

## Decision records

A decision record is the only place that keeps why a boundary exists: the
problem, the rejected alternatives, and the accepted trade-off. Specifications
and `AGENTS.md` state the target state and never narrate history; a reader who
needs the rationale follows the link to the record.

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

These rules govern every specification document, in `docs/`, `design/`, and
`eng/` alike.

- Write active prose in English. Use short sentences, active voice, American
  spelling, and stable terms. Use ASD-STE100 writing rules as a target; do not
  claim compliance. Keep domain identifiers, field names, API names, commands,
  serialized values, and code symbols in their exact spelling. Use `must`,
  `may`, and `must not` for requirements, options, and prohibitions.
- Keep terms consistent with [`CONTEXT.md`](../../CONTEXT.md). Define a term
  once and link to it.
- State normative rules in prose, and use numbered steps for a linear
  procedure.
- Commands and examples must run or parse as written, each one independently.
  The documentation gate cannot prove that a command has the documented
  effect; verify examples against the owning implementation.
- Draw diagrams in Mermaid when a boundary, ownership relation, dependency,
  sequence, hierarchy, or state transition is easier to understand as a
  picture. Prefer `flowchart` and `sequenceDiagram`. Give every arrow a
  meaning, and draw only real concepts. Do not draw when prose is already
  clear. Write key rules in prose, so a diagram is never the only source of
  truth.
- The `text diagram` fence is legacy. Migrate it to Mermaid when you touch its
  document. Use `text literal` for command output, syntax, protocols,
  pseudocode, and data shapes. A bare `text` fence is invalid.
- Do not use raw HTML, including HTML comments. Markdown is the only document
  markup.
- Do not use tables. Give the same information as short prose or one concrete
  example.

## Verification

`npm run docs:check` gates documentation mechanics: Latin-script prose, link
targets, and diagram fences. It must also gate that every decision record
carries a Status line and an `## Alternatives considered` section. Reviewers
enforce the placement and durability rules in this document; no gate can judge
them.

## Status

- The existing records in `design/decisions/` predate the authoring contract.
  They do not carry Status lines and do not follow the section skeleton.
- `npm run docs:check` does not yet gate the Status line or the
  `## Alternatives considered` section of decision records.
- `npm run docs:check` covers `docs/` and `design/` only. It must extend to
  `eng/`; the existing `eng/` documents predate the writing rules.
