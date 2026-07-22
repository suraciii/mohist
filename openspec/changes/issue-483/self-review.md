## Findings

### 1. Legacy Follow-up outbox conversion cannot satisfy the new admission invariant

**Severity: high**

[design.md](design.md:76) says a v1 Runner snapshot converts known successful Follow-up admission to `followup.admitted`, while ambiguous input becomes `followup.delivery.unconfirmed`. It does not define how a legacy `session.input` is classified as `current-turn` versus `new-turn`, how its deterministic `turnId` is shared with the converted admission record, or how the required paired fact is created. The Follow-up spec requires an admitted operation to be committed with either `turn.input.added` or `turn.started` in a prescribed order ([follow-up spec](specs/agent-session-followup-delivery/spec.md:11)); conversion to an admission fact alone violates that contract.

The current v1 outbox shape contains only target, runtime session, generic event payload, work metadata, and sequence ([runtime-event-outbox.ts](../../../packages/runner/src/server/runtime-event-outbox.ts:784)); it has no placement or Turn identity. `T-002` requires atomic conversion but does not add a session-aware conversion algorithm or a test matrix for these legacy combinations ([tasks.json](tasks.json:27)). Define how conversion reads or reconciles Server Session state, maps queued `session.input` plus terminal records as one ordered operation, handles records with no provable placement, and prove that no conversion path emits an admitted fact without its required Turn fact.

### 2. The one-way cutover gate is a required but unspecified and unplanned behavior

**Severity: high**

The design depends on rejecting old Runner delivery during the coordinated deployment ([design.md](design.md:82), [design.md](design.md:91)), but leaves the protocol-version carrier as an open question ([design.md](design.md:101)). `T-001` claims to output a versioned Server contract, and `T-002` says to consume it, but neither task defines the registration/handshake field, mismatch response, runner admission rule, upgrade ordering enforcement, or coverage that proves an old Runner cannot send legacy events to a migrated Server ([tasks.json](tasks.json:20), [tasks.json](tasks.json:45)).

The current Runner source has no protocol-version mechanism. Without a concrete gate, an old Runner can retry a durable legacy event after the Server begins rejecting those names, leaving outbox records permanently blocked or forcing an unsafe compatibility path. Resolve the carrier and failure semantics in the design, then make one task own implementation and Server/Runner contract tests for successful matching registration and rejected mismatches.

<promise>FAIL</promise>
