---
status: converged
---

# Event Protocol

This document defines one Mohist event-envelope protocol. The same router and
expression language can subscribe to important events from any entity. See
[`eventbus.md`](eventbus.md) for persistence and delivery and
[`event-routing.md`](event-routing.md) for the Agent-facing routing table.

## Three Orthogonal Axes

Every event envelope answers three questions through separate properties:

| Axis | Envelope property | Question answered |
|---|---|---|
| What | `type` | What happened? |
| Who | `source` | Which entity emitted it? |
| Where | Context extension attributes | Which business lineage contains it? |

`type` and `source` already have stable conventions. This protocol adds
mandatory **business-lineage context stamping**. It makes "subscribe to
everything under Issue #42" expressible as one predicate.

## `type`: Event Taxonomy

Types use `com.mohist.<domain>.<event>` and are registered in `EventCatalog`.
The Catalog answers only which stable event types exist. An event family and
its structure determine lineage requirements; the Catalog does not duplicate
an attribute schema for every type.

## `source`: Emitting Entity

The source uses the emitting entity's domain identity, such as
`/mohist/workflow-runs/{workflowRunId}`,
`/mohist/projects/{projectId}/issues/{issueNumber}`, or
`/mohist/projects/{projectId}/epics/{epicNumber}`. Project scope is part of an
Issue or Epic identity. Mutable business lineage such as Epic membership or
Workflow origin is not encoded into source.

## Context Attributes: Business-Lineage Stamping

### Rules

1. **Stamp completely at production time**: The store layer writes the flat
   extension attributes from lineage held by the producing aggregate at that
   moment. An Issue uses its own `EpicNumber?`; a WorkflowRun uses its Issue
   context. Stamping must not query another aggregate.
2. **Route by envelope only**: Matchers and dispatchers read only the envelope
   and never query the business domain. A domain reaction handler may read
   current aggregate state before issuing an idempotent command, but that read
   cannot change whether the route matched.
3. **Snapshot truth**: Attributes record ownership at production time. Moving
   an Issue to another Epic does not rewrite historical events.
4. **Admission criterion**: Promote a business identity to an envelope
   attribute when it is valuable as a routing dimension. Payload `data` never
   participates in routing.

### Names

CloudEvents extension names contain only lowercase letters and digits. A
business entity uses the shortest accurate name for its unique identity:

- `projectid`: Global Project identity.
- `issue`, `epic`: Issue or Epic number within a Project and therefore part of
  its domain identity.
- `workflowrunid`, `agentid`, `sessionid`, `runnerid`: The corresponding global
  identities.
- `workspace`: Workspace name within a Project; `projectid` and `workspace`
  together are unique. `workspaceoriginkind` is the creation source:
  `manual`, `issue`, `slack`, or `web`.

An envelope does not carry both `issue` and `issueid`, or both `epic` and
`epicid`. Issues and Epics have no second internal ID, so `issueno` and
`epicno` aliases also do not exist.

### Stamping Matrix

| Event family | projectid | epic | issue | workflowrunid | agentid | sessionid | runnerid |
|---|---|---|---|---|---|---|---|
| `workflow.*` | Required | If present | If present | Required | - | - | - |
| `issue.*` | Required | If present | Required | - | - | - | - |
| `epic.*` | Required | Required | - | - | - | - | - |
| `agent-session.*` | Required | If present for Workflow origin | For Workflow origin | For Workflow origin | For Agent origin | Required | - |
| `runner.*` | If present | - | - | - | - | - | Required |
| `workspace.*` | Required | - | If present | - | - | - | - |
| `inbox.item-persisted` | Required | From source event if present | Required | From source event if present | - | - | - |

"If present" means that production must stamp an existing association and
must omit the attribute rather than stamp an empty value when none exists.

`workspace.*` events also stamp `workspace` and `workspaceoriginkind`. Origin
is a Workspace resolution key, so a subscriber responding in an entry-point
context such as a channel or conversation must be able to filter by it.

Any Workflow event that structurally carries a Stage also stamps `stage`. This
includes `workflow.stage.*`, `workflow.task.*`, `workflow.check.*`, and
`workflow.feedback.requested`. The `{{event.stage}}` rendering placeholder
depends on this attribute and no longer parses `data`.

`subject` keeps its CloudEvents meaning and is not a routing key.

## Match Expressions: CEL Subset

A subscription or route matches an envelope with one Boolean expression. The
syntax is a subset compatible with [CEL](https://cel.dev/). If later needs
outgrow the subset, a complete implementation can replace it without changing
stored expressions.

### Syntax

```text
expr       := or
or         := and ( "||" and )*
and        := unary ( "&&" unary )*
unary      := "!" unary | primary
primary    := "(" expr ")" | comparison | call | presence
comparison := operand ( "==" | "!=" ) operand
            | attr "in" "[" string ( "," string )* "]"
call       := attr "." func "(" string ")"      func in { startsWith, endsWith, contains, matches }
presence   := "has" "(" attr ")"
operand    := attr | string
attr       := "event" "." ident
string     := double-quoted string literal
```

Examples:

```text
event.type.startsWith("com.mohist.workflow.") && event.issue == "42"
event.type == "com.mohist.workflow.run.failed" && event.stage != "plan"
event.issue in ["42", "43"]
event.type == "com.mohist.issue.completed" && has(event.epic)
```

### Semantics

- Every value is a string. `event.<attr>` resolves an envelope property.
  `type`, `source`, `subject`, and every context extension have equal status.
- A **missing attribute evaluates to the empty string `""`**. Use `has()` to
  distinguish missing from empty.
- `matches` performs regular-expression matching with an evaluation timeout.
- There are no loops or function definitions, termination is guaranteed, and
  evaluation is deterministic for the same event and expression.
- **Compile on write**: Reject a create or update when parsing fails. Treat a
  runtime evaluation error as no match and record it in structured logs and a
  counter.
- `event.data.*` is unavailable. Payload structure is private to each domain,
  and routing cannot couple to it. Promote a required business dimension to a
  context attribute under the admission criterion.

### Evaluator

The evaluator is a small internal implementation, estimated at 300-400 lines
plus a conformance suite, with no external dependency. `Cel` and `Cel.NET` are
not used because evaluation targets only a flat string-to-string dictionary,
does not need the CEL type system or protobuf integration, and neither library
is a mainstream community dependency.

## Dispatcher and Consumer Relationship

One router, the single dispatcher in `eventbus.md`, serves two consumer types
through the same protocol:

- **System consumers**: `[Subscription]` handlers registered at compile time.
- **User consumers**: Agent routing tables in `event-routing.md`.

See `eventbus.md` for how matching responsibilities differ between the two
surfaces. **Symmetry is the acceptance criterion**: the protocol is broken if a
system handler can receive an event that no user expression can subscribe to.

## Conformance

- `EventCatalog` maintains event types only and does not own another lineage
  matrix.
- Production rules are defined by aggregate event family. WorkflowRun, Issue,
  Epic, AgentSession, Runner, and Workspace each have required base context.
  Inbox-derived events inherit source-event context. Event structure, not a
  handwritten type list, decides whether to stamp `stage`.
- A spec suite traverses every real event-production path and asserts its
  envelope by producer family and event structure. Forgetting lineage on a new
  producer or emitted event fails the suite without a `CatalogOnlyTypes`
  exception list.
- The expression evaluator has an independent conformance suite for syntax,
  missing attributes, regular-expression timeout, and determinism.

## Status

Implemented: the three-axis envelope and event catalog; business-lineage
stamping with Lineage and ProducerConformance coverage for each production
path; the CEL-subset evaluator and user routing evaluation; promotion of the
`stage` attribute; and Workspace create and archive events
(`com.mohist.workspace.created` and `com.mohist.workspace.archived`) carrying
`workspace` and `workspaceoriginkind`. Issue #412 removed dual Issue and Epic
identities and the old `issueid`, `epicid`, `issueno`, and `epicno` attributes.
