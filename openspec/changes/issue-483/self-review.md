## Findings

### 1. The migration resolver has no durable admission lifetime

**Severity: high**

[design.md](design.md:80) accepts `migration.followup.resolve` only during a "version-2 migration window", but neither the design nor `T-001` defines when that window opens or closes, how it survives Server restart, or how it accounts for offline runners. This conflicts with the migration plan: an old runner is first rejected by the version gate, then upgrades locally, converts its durable v1 snapshot, and only then can send the new control record ([design.md](design.md:97)-[design.md](design.md:100)). A runner that was offline while a finite window closed will retain a valid v2 migration record that the Server refuses, permanently stranding a pending input and violating the no-loss requirement.

Define a durable, restart-safe resolver admission rule. The simplest correct rule is to keep the version-2 migration control record accepted indefinitely as a distinct, idempotent non-legacy command, while continuing to reject all legacy terminal event names; alternatively persist an explicit per-runner/per-snapshot migration fence and retain admission until it is acknowledged. Assign that lifecycle and test coverage to `T-001` and `T-002`, including a runner that upgrades after the initial deployment window.

<promise>FAIL</promise>
