# Design: Generic Reasoning Effort Capability

## Data ownership

The Agent definition owns the optional canonical `reasoningEffort`. The
execution snapshot owns the complete tuple and capability revision. A Runner
registration owns the ephemeral runtime catalog witness. The runtime adapter
owns native translation. No layer copies native Pi names into the generic
definition.

## Resolver ordering

1. Normalize the saved effort to the canonical enum or leave it unset.
2. Read one immutable runner catalog snapshot.
3. Require a complete entry and matching capability revision.
4. Validate model, effort, and variant independently.
5. Return a typed disposition and the compatible runner identity.
6. Only a `supported` disposition may reach claim/dispatch.

The resolver is pure and has no retry, sleep, network call, or side effect.
Readiness is a separate `unavailable` fact; it must not be confused with a
configuration mismatch.

## Alternatives rejected

- **Store native `thinkingLevel` in `variant`:** couples the generic contract
  to Pi and silently loses effort semantics for other runtimes.
- **Let every Runner interpret the saved effort independently:** produces
  different admission decisions and cannot provide one durable explanation.
- **Treat missing catalog data as incompatible:** turns a temporarily absent
  runner registration into a terminal user configuration error.
