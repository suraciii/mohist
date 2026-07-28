## Review

Reviewed issue 500 against `proposal.md`, `design.md`, `tasks.json`, and both capability specs after the plan repair.

- The proposal and specs cover all three issue scopes: grain test controls, dependencies that production composition already guarantees, and AgentSession persistence observation. External behavior remains explicitly unchanged.
- The design now observes one complete `PersistCallback` cycle rather than individual store writes. Its checkpointed reporter records the final `FlushAsync` outcome only after success or failure, distinguishes state/event and transcript failures, and excludes synchronous input-fence and immediate evidence writes that share the transcript store.
- T-001 covers all six forced-deactivation operations and four grain-key overrides, using the existing management-grain collection mechanism. T-002 covers the audited required collaborators while retaining real optional side channels. T-003 removes the flush command and migrates callers to correlated cycle observations without polling or wall-clock waits.
- The task graph is acyclic: T-001 and T-002 are independently executable at priority 1; T-003 depends only on T-001 because both change the AgentSession grain contract. Every task includes test and architecture-verification criteria.

No material planning gaps found.

<promise>PASS</promise>
