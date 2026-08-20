# Self-Review: Issue 618

Review mode: re-review. I re-read the live issue with `mo issue view 618 --project proj_f6c141d63b6243bfbb481737b2243b87 --json body` before checking the revised artifacts. The issue has eight acceptance criteria; this review verifies the previous findings and checks regressions introduced by their dispositions.

**Verdict: FAIL**

## Must-Fix Findings

### 1. Restart and credential-expiry reissuance is not a defined lifecycle

**Violates:** Issue Acceptance Criterion 5: after a Server restart, Session recovery, or credential expiry, the system must reauthorize from the immutable Slack origin and issue a new credential without reusing the old value.

The revised design defines issuance for new, recovered, and replacement executions and says that lease-store shutdown revokes active leases (`design.md:90-96`). It does not define the required behavior for a Server restart that is not a graceful lease-store shutdown, nor does it bind lease validity to a Server incarnation. The deployment topology and lease-store sharing remain open (`design.md:149-150`). With a shared runtime store, an old hash can remain valid across a restart unless a restart epoch or equivalent invalidation boundary is specified; with an in-memory store, the Runner can still hold the old grant and broker unless the durable recovery transition is explicitly triggered.

The expiry path is also incomplete. The only specified behavior for an expired credential is rejection (`manager-execution-credentials/spec.md`, `Credential is expired` scenario). The design leaves open whether an active execution is renewed only by starting a new turn (`design.md:149`), while fresh issuance is otherwise described only for a new follow-up or recovered execution (`design.md:90`). A Manager turn that reaches expiry therefore has no defined path from the rejected invocation back to origin-based reauthorization and a new execution credential.

Before build, the plan must define one authoritative restart boundary for all Server topologies, invalidate old Runner grants and brokers across graceful and ungraceful restart, and specify what durable recovery/new-turn transition handles an expired active execution. It must state that a state-changing operation with an uncertain result is never automatically replayed, and add tests for restart, crash recovery, expiry during a turn, and fresh values after each path. T-003's current acceptance text (`tasks.json:52-57`) does not establish those behaviors.

### 2. The claimed per-execution CLI boundary is not connected to the current OpenCode execution boundary

**Violates:** Issue Acceptance Criterion 4: credentials must be execution-scoped and absent from prompts, transcripts, logs, Session state, and durable records while remaining available to the Manager capability execution.

The design specifies a private broker, a per-execution `mo` launcher, and a private `PATH`, then asserts that Pi and OpenCode use the same boundary (`design.md:92-96`; `tasks.json:54-57`). That assertion does not match the current Runner/OpenCode contract. The Runner's process helper applies an `env` override only when the Runner itself spawns the child (`packages/runner/src/system/process.ts:94-128`). OpenCode is instead started through the SDK's `createOpencodeServer` without a per-turn environment or command-boundary parameter (`packages/runner/src/runtime/opencode/server-process.ts:4-11,57-66`), and `OpenCodeRuntime` retains a shared server/client while turns send prompts through that client (`packages/runner/src/runtime/opencode/runtime.ts:111-157`; `packages/runner/src/runtime/opencode/turn.ts:172-203`). A launcher placed in a per-execution Runner environment is consequently not shown to the OpenCode server's model-shell child. Making it global would allow cross-execution access to a credential and violate the same criterion.

Capability/version gating alone is not sufficient: the built-in Manager currently selects OpenCode (`packages/server/src/Mohist.Server/Agent/Services/BuiltInAgentCatalog.cs:12-21`), so rejecting the runtime that cannot preserve the boundary would leave the required Manager flow unavailable. The plan must choose and specify a concrete OpenCode-compatible boundary, such as an isolated per-execution OpenCode process with its own broker environment or an explicit Runner-mediated CLI boundary that preserves the `mo` surface. It must define concurrency, cleanup, failure-closed behavior, and redaction at that boundary, and the integration tests must exercise the actual Pi and OpenCode command paths rather than only testing a Runner launcher in isolation.

## Previous Findings

- **Previous credential transport finding: fixed properly for the previously identified gap.** The revised plan now names a non-durable `managerExecutionGrant`, keeps plaintext values outside `WorkDispatch` and durable records, specifies a private broker/launcher, and describes cleanup and redaction (`design.md:90-98`). The restart/expiry lifecycle finding above is a remaining contract gap, not a reassertion that the carrier is unspecified.
- **Previous Manager reply-route finding: fixed properly.** The revised plan now specifies a dedicated reply route, separate reply lease, full-origin validation, synthetic Manager owner derivation, exact progress promotion/deduplication, and route/liveness tests (`design.md:64-66`; `tasks.json:71-78`).

## Dimension Checks

- Issue goals and acceptance criteria: checked against the live issue; the eight criteria are represented in the proposal, specs, and task acceptance text, but Criteria 4 and 5 remain unsatisfied by the two boundary gaps above.
- Coverage: checked; the revised artifacts cover natural-language replies, protocol removal, loop prevention, allowlisting, current authorization, credentials, recovery, and liveness. The restart/expiry and OpenCode execution details are not complete enough to make the corresponding coverage implementable.
- Correctness: checked; the Manager-owned reply path and ordinary-session direction are coherent, but the credential behavior cannot guarantee the issue's restart/expiry or OpenCode secrecy requirements as written.
- Consistency with the current codebase: checked; the reply/outbox design now addresses the existing Manager ownership split. The proposed per-execution PATH/broker is not yet reconciled with the shared SDK-managed OpenCode server boundary cited above.
- Task breakdown, ordering, and verifiability: checked; T-003 and its security tests name the required cases, but they cannot pass until the restart/expiry state transition and OpenCode injection boundary are selected. T-001 and T-002 are parallel despite T-001 removing or unregistering the old Manager executor while T-002 may move reusable operation behavior out of it; this is an ordering watchpoint, not an additional must-fix finding because the design also permits reuse of the existing application services.

## Observations

- The allowlist is still expressed as logical categories rather than one canonical list of operation ids, argument schemas, and route mappings (`design.md:70-82`; `tasks.json:30-36`). The owner claim/transfer choice also remains open (`design.md:151`). Tests should make the catalog authoritative and prevent CLI and Server drift.
- Exact credential TTL, clock-skew handling, and shared lease-store implementation remain open (`design.md:149-150`). The lifecycle semantics are a must-fix because of Criterion 5; the numeric policy and topology choice are observations once that lifecycle is explicitly defined.
- Adapter support for idempotent reaction add/remove remains an open question (`design.md:152`). The implementation should either prove the supported adapter contract or gate Manager liveness before enabling it; this is not a separate must-fix finding here because the task already requires adapter integration coverage.
- The proposal calls the change a non-general-purpose Manager HTTP API while the design adds a dedicated Manager reply endpoint (`proposal.md:40`; `design.md:64`). Clarify that this is an internal, narrowly scoped reply capability route rather than a public management API.

<promise>FAIL</promise>
