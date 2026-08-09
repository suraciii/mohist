# Agents — Writing Design Specs

`design/` is the design spec layer: why the system has its boundaries and which
contracts an implementation must preserve. It is written for developers and
agents who implement the design, not for readers tracing the current code.

## Rules

- Write the spec before implementing. Current code does not decide the target design.
- Start with the problem, design forces, and chosen trade-off. Explain why the
  boundary exists before describing its mechanics.
- Keep models minimal: only concepts and fields the current behavior needs.
- Define what a concept is and what it is not; state ownership, scope, identity, lifecycle, invariants.
- Write deterministic rules: order, timing, write targets, failure behavior. Reject illegal states, do not fail silently.
- Do not rewrite classes, methods, call chains, or storage operations as prose.
  Mention a code symbol only when it names a durable implementation boundary or
  links a current gap to its source.
- One noun, one meaning. One rule defined in one doc; others link it.
- Technical language is allowed here; define terms a new reader may not know.
- Use the minimal structure: Design Drivers / Model / Semantics / Examples / Status.
- Before writing, a reader must know: which problem is being solved, why this
  boundary was chosen, who owns it, how it changes, and what fails.
- Body is the target design; implementation gaps go to Status.
- Every plain-text fence must be either `text diagram` for an ASCII boundary,
  relationship, sequence, hierarchy, or state diagram, or `text literal` for
  command output, syntax, protocols, pseudocode, data shapes, and other
  preformatted text. Bare `text` fences are invalid.
- Diagrams use ASCII only. Do not use PlantUML, Mermaid, Unicode line art, or
  Unicode arrows. Do not use raw HTML.

Full conventions: [`design/README.md`](README.md).
