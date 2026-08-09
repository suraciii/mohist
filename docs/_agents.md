# Agents: Writing Product Docs

`docs/` is the product specification layer. It defines what the product must
satisfy. Write for users and Agents who do not read the source code.

## Rules

- Write the spec before implementing.
- Lead with the user problem and why the product behavior exists. Explain the
  constraint or trade-off that makes the rule necessary before listing the
  rule itself.
- Use only product and domain language. Do not use APIs, fields, components, or
  source paths in the body.
- Describe the product model and user-visible contract, not the code that
  happens to implement it. Do not turn classes, methods, handlers, or storage
  steps into prose.
- One section, one purpose. Lists over paragraphs, tables over lists.
- Define a rule once; other docs link it, never copy it.
- Commands and examples must be runnable as written.
- Keep exact commands and field-by-field details in task guides and reference
  sections. Concept sections explain the larger workflow, ownership boundary,
  and reason for the design.
- The body is the spec. Put divergence in an Implementation Gaps section, never
  in a status list.
- Keep terms consistent with [`CONTEXT.md`](../CONTEXT.md).
- Write active prose in English. Use short sentences, active voice, American
  spelling, and `must`, `may`, or `must not`. Treat ASD-STE100 as a writing
  target, not a compliance claim.
- Use fenced `text` blocks and ASCII characters for diagrams. Add a diagram only
  when it clarifies a boundary, ownership relation, dependency, sequence,
  hierarchy, or state transition. State the normative rule in prose.
- WIP product ideas stay in a WIP section with `status: wip-not-implemented`.

Full conventions: [`docs/README.md`](README.md).
