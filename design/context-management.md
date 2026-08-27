# Context Management

This document specifies how the Mohist repository manages context: what the
repository stores, what it must not store, and where each kind of information
lives. It governs every file whose purpose is to inform future readers —
humans and agents — rather than to execute.

## Design drivers

The repository is the primary context source for every agent session and
every contributor. Two failure modes follow from that role:

- **Missing durable context.** Rules, terms, and decisions that exist only in
  chat history, closed Issues, or merged pull requests are re-derived
  inconsistently, or lost.
- **Accumulated transient context.** Plans, research dumps, and progress logs
  describe a moment. When the moment passes, nothing maintains or deletes
  them, and they mislead later readers with stale facts that still look
  authoritative.

Both failures cost the same resource: the reader's trust that what the
repository says is true.

## Principles

**The repository holds durable context only.** Durable context stays true
indefinitely and changes only when the product or the design changes: the
product specification, the design specification, the glossary, standing
rules, and decision records.

**Transient context never enters the repository.** Transient context
describes momentary state: plans, research notes, progress logs, review
drafts, probe output, one-off measurements, and runtime state. Its home is
the Mohist Issue and the parent workspace, which maintain and retire it as
work proceeds.

**The specification is the delta.** A change to the product or the design is
expressed by editing the specification itself. The pull request diff is the
change record, and review happens on the Issue and the pull request. The
repository must not grow an in-repo proposal, change-tracking, or planning
format. The OpenSpec tree is retired; do not extend it and do not replace it
with another in-repo format.

## Where context lives

One fact has one home. Other documents link to the home; they never restate
the fact.

```text diagram
AGENTS.md                 Global standing rules; routes, never explains
CONTEXT.md                Glossary: the single entry point for terms
docs/                     Product specification (user-facing)
  AGENTS.md               Product-spec writing rules
design/                   Design specification (developer-facing)
  AGENTS.md               Design-spec writing rules
  decisions/              Decision records, each with a Status line
packages/<pkg>/AGENTS.md  Package-scoped standing rules, when they exist
code comments             Narrow-scope technical detail: why, never what
```

- **Root `AGENTS.md`** holds only rules that apply across the whole
  repository. Each rule links to the document that owns the detail.
- **Scoped rule files are named `AGENTS.md`.** Agent tooling loads
  `AGENTS.md` files automatically when work enters their tree; a rules file
  that loads itself cannot be forgotten. Names such as `_agents.md` or
  `agents.md` hide the same content from that mechanism and must not be
  used.
- **`design/decisions/`** holds durable decision records: the alternatives a
  change rejected and the trade-off it accepted. Each record carries a
  Status line: `Status: accepted`, or `Status: superseded by <link>`
  pointing at the record that replaced it. A superseded record stays
  readable; the Status line is the only edit it receives.

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

## Verification

`npm run docs:check` gates documentation mechanics: English prose, link
targets, and diagram formats. Reviewers enforce the placement and durability
rules in this document; no gate can judge them.
