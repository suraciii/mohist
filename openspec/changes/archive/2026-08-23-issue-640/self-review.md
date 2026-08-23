# Self-Review (re-review): issue-640

## Verdict

PASS — the prior must-fix has been fully addressed, the repair introduces no must-fix regression, and the plan is ready to build.

## Prior findings — disposition

### MF-1 — RESOLVED

The previous review found that a Workflow-key-only wait could admit cleanup attempt 2 or later while the preceding cleanup turn's Session-scoped terminal facts were still retained.

The repaired plan now models the immediate predecessor explicitly:

- Cleanup attempt 1 waits for every retained `workflow-session` record under the original Workflow scheduling identity (`design.md:92-98`; `specs/runtime-event-delivery-wait/spec.md:3,10-14`).
- Cleanup attempt 2+ uses `workflowCleanupOperationId(..., cleanupAttempt - 1)` and waits for both the prior `workflow-cleanup` boundary and every `session-followup` record carrying that operation id (`design.md:99-107,159-170`; `specs/runtime-event-delivery-wait/spec.md:16-20`).
- The cleanup-admission spec now has explicit later-attempt scenarios for both OpenCode and Pi (`specs/cleanup-turn-admission/spec.md:19-29`) and requires every bounded attempt to remain usable under predecessor-delivery lag (`specs/cleanup-turn-admission/spec.md:62-67`).
- T-001 and T-002 require attempt-2+ regression coverage, including the critical state where the Workflow-keyed cleanup boundary has settled but a correlated Session-scoped terminal fact remains retained (`tasks.json:11-17,33-40`).

This matches the current reporter implementation: `WorkflowAgentSessionReporter` derives the deterministic cleanup operation id, stamps it on the cleanup runtime input and all cleanup-produced `session-followup` facts, and retains the Session turn as their owner. The repaired correlation therefore observes the records that actually make the preceding cleanup turn terminal server-side.

### Prior observation: optional port method — remains an observation

The method remains optional so lightweight test doubles need not all change (`design.md:178-182`; `tasks.json:25,50`). This weakens compile-time protection for hypothetical future outbox replacements, but it does not make the current plan incomplete: the production outbox is required to implement the method, and production admission tests must exercise both predecessor forms through the real implementation.

### Prior observation: retention terminology — RESOLVED

The artifacts no longer call retention-cap drops acknowledgements. They define completion as durable acknowledgement removal or applicable deterministic-refusal settlement, while preserving the existing rule that only reconstructible streaming deltas may be removed under retention pressure and boundary/terminal convergence records remain fail-closed (`design.md:108-113`; `tasks.json:13,25`).

## Regression and consistency check

- **Issue goal:** The issue record was re-read first. It remains title-only with no body or separately enumerated acceptance criteria. The repaired plan directly satisfies the stated goal: cleanup admission no longer fails because the immediately preceding turn's convergence facts are delivered late.
- **Both runtimes:** The wait is placed before `openWorkflowAgentSession` in both OpenCode and Pi, which addresses the OpenCode projection guard and the Pi frozen-binding conflict at their actual admission boundaries (`design.md:157-176`).
- **Fail-closed preservation:** Only positive same-work-item cleanup attempts bypass the OpenCode unsettled-projection guard. Non-cleanup admission retains the existing `session-binding-failed` behavior, and the Server cleanup route and frozen-binding validation remain unchanged (`design.md:192-207,242-249`; `specs/cleanup-turn-admission/spec.md:48-56,69-72`).
- **Failure evidence:** Timeout handling is traced end to end: typed outbox error, declared `session-delivery-wait-timeout` action code in both manifests, and preservation by worktree enforcement rather than conversion to `worktree-dirty` (`design.md:209-229`; `tasks.json:37`).
- **Non-polling semantics:** Waiter resolution is tied to durable settlement/removal and recovered-state loading. The budget timer only fails the wait; it does not poll or re-evaluate retained state (`design.md:114-126`; `specs/runtime-event-delivery-wait/spec.md:22-31`).
- **Task breakdown:** T-001 provides the outbox primitive and focused fake-timer tests; T-002 consumes it at both admission sites and verifies runtime/worktree behavior. T-002 depends on T-001, the graph is acyclic, and the ordering is implementation-safe and verifiable.
- **Mechanical checks:** `tasks.json` parses successfully; both task spec anchors resolve to actual requirement headings; the dependency graph is valid; `git diff --check` passes.

## Observations

- T-001's title still says “Workflow session delivery wait” although its repaired scope crosses Workflow-scoped and Session-scoped producer families. Its description, acceptance criteria, output, and spec pointer are precise, so the title does not misdirect implementation.
- The design's Open Questions retain future tuning questions about configurability, the 60-second value, and failure-code naming even though D4/D5 and `tasks.json` pin the build contract. Builders should follow the pinned decisions; the questions are post-build reconsiderations, not unresolved blockers.

<promise>PASS</promise>