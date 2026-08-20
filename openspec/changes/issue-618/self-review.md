# Self-Review: Issue 618

Review mode: re-review. I read the live issue with `mo issue view 618 --project proj_f6c141d63b6243bfbb481737b2243b87 --json body` before reviewing the revised artifacts. The issue has eight acceptance criteria covering the natural-language Manager flow, reply ownership, loop prevention, runtime-only credentials, restart/session/expiry recovery, current authorization, the capability allowlist, and terminal reaction liveness.

## Previous Findings

1. **Restart and credential-expiry lifecycle: fixed properly.** The prior finding required an authoritative restart boundary, invalidation across graceful and ungraceful restart, origin-based fresh recovery, explicit expiry handling, and no replay after an uncertain mutation. The revised design defines the shared deployment epoch, graceful revocation, crash invalidation, lease-store fail-closed behavior, Runner reconciliation, expiry closure, and fresh recovery values (`design.md:88-102`). The credential spec now has scenarios for graceful shutdown, crash recovery, expiry during a turn, expiry reauthorization, and uncertain state-changing results (`specs/manager-execution-credentials/spec.md:20-41`). T-003 makes these behaviors testable, including exactly-once recovery and fresh values (`tasks.json:50-57`). This satisfies issue Acceptance Criterion 5 and does not regress the no-side-effect requirement in Criterion 6.

2. **OpenCode execution boundary: fixed properly.** The prior finding was that the existing built-in Manager selects OpenCode while the shared SDK-managed OpenCode server cannot receive a per-turn environment. The revised design selects a concrete isolated per-execution OpenCode server/client path, passes the scoped boundary at process spawn, forbids fallback to the shared runtime, and gates dispatch when the boundary cannot be installed (`design.md:94-102`). T-003 requires real Pi and isolated OpenCode process tests, generic-shell isolation, cleanup, redaction, and mixed-version gating (`tasks.json:50-57`). This now satisfies the execution-scoped secrecy requirement in issue Acceptance Criterion 4.

## Dimension Checks

- **Issue goals and acceptance criteria: checked, no issue.** The eight live criteria are represented by the four capability specs and task acceptance criteria. The list-Agent flow is covered by the allowlisted list capability, ordinary Agent execution, Agent-owned reply action, and shared liveness path.
- **Coverage: checked, no issue.** The plan covers initial and follow-up Sessions, replacement/recovery, natural-language output, retired-protocol removal, current authorization, protected operations, per-execution credentials, Pi/OpenCode boundaries, loop prevention, receipt/progress/terminal convergence, and existing non-Manager regressions.
- **Correctness: checked, no issue.** The reply lease is separate from the management lease; the Manager route derives its owner and project from the validated origin; Server terminal delivery only finalizes liveness; every invocation revalidates current authorization; and uncertain mutations are classified unknown without automatic replay.
- **Consistency with the current codebase and conventions: checked, no issue.** The plan directly addresses the current `SlackManagerConversationService` synthesized responses, `SlackManagerToolTurnProcessor` terminal parser, shared `SlackStatusProjection`/outbox primitives, existing `AgentSlackExecutionContext`, CLI command surface, and shared OpenCode runtime. It preserves ordinary Connection routes and application-service authorization paths.
- **Task breakdown, ordering, and verifiability: checked, no issue.** The four tasks form an acyclic graph: Session/CLI work precedes credential transport, and liveness depends on the Session and credential contracts. Each task has a spec anchor, implementation output, acceptance criteria, and focused tests for its failure modes. T-001 and T-002 can be developed independently because the design permits the supported operations to be moved to or reused through existing application services rather than requiring the retired executor.

## Observations

- The exact credential TTL and clock-skew allowance remain open in `design.md:153-156`; the lifecycle and invalidation semantics are fixed, so this does not block the issue criteria.
- The final owner claim/transfer catalog and delivery of any one-time claim value remain open (`design.md:155-157`). The plan correctly keeps this conditional and requires protected-value redaction.
- Adapter support for idempotent reaction add/remove remains an open compatibility question (`design.md:157`). The planned adapter integration tests and capability gate address the runtime dependency; the issue does not require a particular adapter implementation.
- The rollback plan retains the retired execution-fence schema temporarily (`design.md:144-151`). This is migration hygiene and does not preserve the retired model protocol or affect the acceptance criteria.

No must-fix findings remain. The observations do not affect the verdict.

**Verdict: PASS**

<promise>PASS</promise>