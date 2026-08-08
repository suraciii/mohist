# Agents — Writing Design Specs

`design/` is the design spec layer: how the system is implemented. Written for developers and agents who implement.

## Rules

- Write the spec before implementing. Current code does not decide the target design.
- Keep models minimal: only concepts and fields the current behavior needs.
- Define what a concept is and what it is not; state ownership, scope, identity, lifecycle, invariants.
- Write deterministic rules: order, timing, write targets, failure behavior. Reject illegal states, do not fail silently.
- One noun, one meaning. One rule defined in one doc; others link it.
- Technical language is allowed here; define terms a new reader may not know.
- Use the minimal structure: Model / Semantics / Examples / Status.
- Before writing, a reader must know: what it is, who owns it, how it changes, what fails.
- Body is the target design; implementation gaps go to Status.

Full conventions: [`design/README.md`](README.md).
