## Why

Mohist events now carry canonical business lineage, but users still lack one reusable way to describe the events they care about or verify that intent before attaching automation. A deterministic, envelope-only matching language and a live filtered tail make those expressions observable now and establish the contract that later event routing can reuse.

## What Changes

- Add a CEL-compatible event matching subset over event type, source, and lineage attributes, supporting `==`, `!=`, `&&`, `||`, `!`, `in`, `startsWith`, `endsWith`, `contains`, `matches`, and `has()`.
- Reject invalid expressions before use with a diagnostic that identifies the error location. Evaluation is deterministic and terminating; regular-expression evaluation is bounded, and runtime evaluation failures are treated as non-matches.
- Define all match values as strings. Missing attributes compare as `""`, while `has()` distinguishes an absent attribute from one that is present with an empty value.
- Restrict matching to the event envelope. Event payload data is not addressable; business dimensions needed for matching must be promoted to lineage attributes.
- Add `mo events tail --match <expr>` to follow the selected project's live event stream and emit only matching events, giving operators a direct way to validate an expression before using it in later automation.
- **BREAKING**: Consolidate event commands under the plural `mo events` noun. Existing `mo event dead-letter ...` commands move to `mo events dead-letter ...`, and the singular top-level noun is removed.
- Do not add routing tables, Agent triggers, durable event queries, or replay in this change.

## Capabilities

- `event-envelope-matching`: Parsing, validation, attribute and presence semantics, supported operators and functions, deterministic evaluation, bounded regular expressions, diagnostics, and the prohibition on payload access.
- `project-event-tail`: Strictly project-scoped live event observation through `mo events tail`, including match submission, filtered delivery, output, cancellation, and error behavior.

## Impact

- **Server** (`packages/server`): event infrastructure gains the reusable compiled matcher; the live event delivery surface gains strict project-scoped expression registration and filtering without changing the finite Activity event feed or durable domain dispatch behavior.
- **CLI** (`packages/cli`): adds the streaming `events tail` command and its output/error handling; moves dead-letter operations from `mo event` to `mo events`.
- **APIs and protocol**: extends the live event subscription contract to carry and validate a match expression against canonical CloudEvent envelope attributes. Persisted event payloads and producer contracts are unchanged.
- **Dependencies and tests**: no external CEL runtime is introduced. Matcher conformance, regex timeout, project isolation, live filtering, diagnostics, and CLI cancellation require focused unit and spec coverage.
