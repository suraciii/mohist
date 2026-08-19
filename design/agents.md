# Agents — Writing Design Specs

`design/` is the design spec layer: why the system has its boundaries and which
contracts an implementation must preserve. It is written for developers and
agents who implement the design, not for readers tracing the current code.

Write all active design prose in English. Use short sentences, active voice, American spelling, and stable
terms. Use ASD-STE100 writing rules as a target. Do not claim compliance. Keep domain identifiers, field
names, API names, commands, serialized values, and code symbols as-is when their exact spelling is part of
the contract. Use `must`, `may`, and `must not` for requirements, options, and prohibitions.

## Writing a design spec

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
- Record rejected alternatives only when they could reasonably return in a later change. State the
  reason for rejection, not the history of the discussion.
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

- Prefer short sentences. One sentence states one rule.
- Prefer domain nouns and product nouns. Use technical nouns only in implementation design, and
  define terms a new reader may not know.
- Use canonical names. Keep casing, singular/plural, and field paths consistent.
- Every plain-text fence must choose exactly one semantic marker: `text diagram` or `text literal`.
- Use `text diagram` only when an ASCII diagram makes a boundary, ownership relation, dependency,
  sequence, hierarchy, or state transition easier to understand. Do not draw when prose is already clear.
- Use `text literal` for command output, syntax, protocols, pseudocode, data shapes, and other
  preformatted text that is not a diagram. Bare `text` fences are invalid.
- Use only ASCII characters in diagrams. Do not add PlantUML, Mermaid, Unicode line art, or Unicode arrows.
- Do not use raw HTML. Markdown is the only document markup.
- Draw only real concepts. Give every arrow a meaning.
- Write key rules in prose. Do not make a diagram the only source of truth.
- Use pseudocode for definite computations.
- Use minimal input/output examples when ambiguity must be resolved.
- Make examples behave like tests. Keep only examples that distinguish between readings.
- Ensure YAML, JSON, command, and API examples parse or run as written.

### Use the minimal structure

Start from the structure below. Delete sections that have no content. Do not add empty sections for symmetry.

```text literal
# Name

The problem and why a design decision is necessary.

## Design Drivers
Constraints, forces, chosen trade-offs, and rejected alternatives that may recur.

## Model
Resources, ownership, references, and the minimal data shape.

## Semantics
Selection, merging, state changes, timing, errors, and interfaces.

## Examples
A small number of inputs and expected outputs.

## Status
Open questions and current implementation gaps.
```

Put API, Writes, Merge, and similar topics in `Semantics` subsections. Split them into standalone sections only when they are complex enough.

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
