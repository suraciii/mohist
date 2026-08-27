# Agents: Writing Product Docs

`docs/` is the product specification layer. It defines what the product must
satisfy. Write for users and Agents who do not read the source code.

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
- Define a rule once; other docs link it, never copy it.
- Commands and examples must be runnable as written, each one independently.
  An example must not depend on an instruction to replace a value shown
  earlier.
- The body is the spec. If the implementation differs materially from a
  document, add an Implementation Gaps section that states the current state
  as a plain product fact. Never put divergence in a status list or delivery
  ledger, and do not reduce the body to a current-feature list.
- Check gaps before changing facts. Before you change a factual statement,
  check whether the document's Implementation Gaps section already records the
  difference. Do not change a target spec back to current behavior.
- Keep terms consistent with [`CONTEXT.md`](../CONTEXT.md).
- Write active prose in English. Use short sentences, active voice, American
  spelling, and `must`, `may`, or `must not`. Preserve the exact spelling of
  product terms, configuration fields, commands, identifiers, and code
  symbols. Treat ASD-STE100 as a writing target, not a compliance claim.
- Use `text diagram` for ASCII visualizations of boundaries, ownership,
  dependencies, sequences, hierarchies, and state transitions. Use `text
  literal` for command output, syntax, protocol examples, pseudocode, and user
  text. Do not use a bare `text` fence. State normative rules in prose, and
  use numbered steps for a linear procedure. Do not use tables; give the same
  information as short prose or one concrete example.
- Do not use raw HTML, including HTML comments.
- Treat `npm run docs:check` as a structural gate. It enforces Latin-script
  prose, Markdown-only structure, text-fence classification, ASCII diagrams,
  and local links. It cannot prove that prose is English or that a command has
  the documented effect. Verify CLI examples against current help and focused
  command tests, and verify behavioral claims against the owning
  implementation.
- WIP product ideas stay in a WIP section with `status: wip-not-implemented`
  frontmatter and future-state language. After the requirements and spec are
  final, move the document to its product area and remove the WIP marker.
