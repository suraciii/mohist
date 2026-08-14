# Generic Agent Reasoning Capability

## Design pressure

`reasoningEffort` is a user-facing execution choice, while a runtime's native
thinking level is an adapter detail. The current catalog exposes models and
variants only, and the Pi registration has previously placed native thinking
levels in the variant map. That makes a saved effort either silently unused or
incorrectly coupled to one runtime.

## Canonical execution tuple

Every prepared Agent execution freezes one tuple:

```text literal
(runtime, model, reasoningEffort, variant)
```

`reasoningEffort` is one of `off`, `minimal`, `low`, `medium`, `high`, `xhigh`,
or `max`, or is unset. It is independent from `variant`; an effort is never
encoded as a variant and a variant is never interpreted as an effort.

The tuple is frozen in the durable dispatch snapshot. Later catalog changes do
not rewrite an existing dispatch. A capability revision is stored beside the
tuple so a Runner can reject a stale snapshot rather than silently changing
its meaning.

## Versioned runtime catalog

Each runtime catalog entry reports:

- `models`: model identities known by the runtime;
- `variants`: variant values by model, independent of effort;
- `reasoningEfforts`: canonical effort values by model;
- `supportsReasoningEffort`: whether this runtime has an effort adapter; and
- `complete` plus `capabilityRevision`: whether the entry is authoritative and
  which immutable catalog revision produced it.

Legacy or incomplete entries are not proof of support. A missing entry is
`needs-setup` while a complete entry that explicitly rejects a tuple is
`unsupported_execution_configuration` or
`incompatible_execution_configuration` as described below.

## One resolver, two adapters

The Server owns a pure resolver for the frozen tuple and the selected runner's
catalog witness. It returns one of:

- `supported`: tuple and capability revision match;
- `needs-setup`: catalog or capability revision is unavailable;
- `unavailable`: the runtime is known but not ready for admission;
- `unsupported_execution_configuration`: the runtime explicitly does not
  support reasoning effort; or
- `incompatible_execution_configuration`: model, effort, or variant is
  explicitly absent from a complete catalog.

Only `supported` may be admitted. `needs-setup` and `unavailable` remain
pending; they are not terminal failures. The two explicit configuration
errors are deterministic preflight failures and must be recorded with the
frozen tuple.

The Runner owns runtime-native adapters. Pi maps canonical effort to its
private `thinkingLevel` input. Other runtimes may provide a different adapter;
none may receive a Pi-specific value through the generic `variant` field.

## Implementation boundary

This document defines the contract only. The executable slice must add the
append-only wire fields, the canonical resolver, snapshot propagation, and
adapter-focused tests in one change. Admission must consume the resolver on
the same runner/capability snapshot; a catalog-only write path is not a valid
implementation.
