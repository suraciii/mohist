# Generic Agent Reasoning Capability

A reasoning effort is a generic Agent execution choice. A Runtime adapts that
choice to its native thinking setting. The generic contract must not depend on
Pi, or encode an effort as a Runtime variant.

## Design Drivers

- The user selects `reasoningEffort`; the Runtime owns its native setting.
- Model, effort, and variant have separate meanings and failure modes.
- A dispatch must keep the capability meaning it had when it was created.
- Admission must distinguish missing capability evidence from an explicit
  unsupported configuration.

## Model

### Canonical execution tuple

Every prepared Agent execution freezes one tuple:

```text literal
(runtime, model, reasoningEffort, variant)
```

`reasoningEffort` is optional. When set, it is one of `off`, `minimal`, `low`,
`medium`, `high`, `xhigh`, or `max`. It is independent from `variant`. An
effort is never encoded as a variant, and a variant is never interpreted as an
effort.

The tuple and a capability revision belong to the durable dispatch snapshot.
Later catalog changes do not rewrite an existing dispatch. The Runner rejects
a stale snapshot rather than silently changing its meaning.

### Runtime catalog

Each Runtime catalog entry reports:

- `models`: model identities known by the Runtime.
- `variants`: variant values by model, independent of effort.
- `reasoningEfforts`: canonical effort values by model.
- `supportsReasoningEffort`: whether the Runtime has an effort adapter.
- `complete` and `capabilityRevision`: whether the entry is authoritative and
  which immutable catalog revision produced it.

A legacy or incomplete entry is not proof of support. A missing entry means
`needs-setup`. A complete entry that rejects a tuple means an explicit
configuration error.

## Semantics

### Resolution

The Server owns one pure resolver. It receives the frozen tuple and the
selected Runner's catalog witness.

```text diagram
                                +------------------------+
                                | frozen tuple + catalog |
                                |        witness         |
                                +------------+-----------+
                                             |
                                             v
                                    +-----------------+
                                    | Server resolver |
                                    +--------++-------+
       +--------------------+----------------++-----------------+------------------+
       v                    v                 v                 v                  v
 +-----------+       +-------------+   +-------------+   +-------------+   +--------------+
 | supported |       | needs-setup |   | unavailable |   | unsupported |   | incompatible |
 +-----+-----+       +------+------+   +------+------+   +------+------+   +-------+------+
       +---+                +--------+--------+                 +--------+---------+
           v                         v                                   v
       +-------+            +----------------+             +--------------------------+
       | admit |            | remain pending |             | record preflight failure |
       +-------+            +----------------+             +--------------------------+
```

The resolver returns one of:

- `supported`: the tuple and capability revision match.
- `needs-setup`: the catalog or capability revision is unavailable.
- `unavailable`: the Runtime is known but not ready for admission.
- `unsupported_execution_configuration`: the Runtime explicitly does not
  support reasoning effort.
- `incompatible_execution_configuration`: the complete catalog explicitly
  lacks the model, effort, or variant.

Only `supported` is admitted. `needs-setup` and `unavailable` remain pending;
they are not terminal failures. The two explicit configuration errors are
deterministic preflight failures and record the frozen tuple.

### Runtime adapters

The Runner owns Runtime-native adapters. Pi maps canonical effort to its
private `thinkingLevel` input. Another Runtime may use another adapter. No
Runtime receives a Pi-specific value through generic `variant`.
