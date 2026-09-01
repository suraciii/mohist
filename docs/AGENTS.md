# Agents: Writing Product Docs

`docs/` is the product specification layer. It defines what the product must
satisfy. Write for users and Agents who do not read the source code.

Shared writing rules (language, diagrams, fences, tables, examples) live in
[`../eng/context-management.md`](../eng/context-management.md#writing-rules).

## Rules

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

## Minimal structure

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
