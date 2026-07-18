# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The `opencode-model-catalog` spec was implemented across T-002 (catalog loading via v2 list APIs, OpenCode as final authority) and T-004 (first-slash model parsing, per-turn model/variant application without rotation, inert legacy keys / type-invalid errors), but neither task referenced it in its `spec` field. Every other spec was referenced; this one was the lone traceability gap, leaving "all specs have tasks" implicit rather than explicit.
  Verification: Added `specs/opencode-model-catalog/spec.md` to the `spec` field of T-002 and T-004 (each implements a disjoint subset of that spec's requirements). Re-validated `tasks.json`: valid JSON, DAG acyclic, all `dependsOn` point to strictly-lower-priority tasks, every task has acceptance criteria with `passes: false`, and a programmatic check now reports zero unreferenced specs.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-001 pins `@opencode-ai/sdk` and smoke-verifies the asserted call surface against a real OpenCode. That smoke step is environment-dependent (needs a real OpenCode CLI + provider); under AFK execution without a real OpenCode present the task can only pin the version and record the asserted surface to verify later.
  SuggestedAction: During T-001 execution, if a real OpenCode is unavailable in the environment, record the asserted call surface and the exact pinned version and treat the smoke as a deferred manual verification rather than blocking downstream tasks on an assumption.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-004 is a large slice — it bundles turn execution, SDK model-DTO construction + per-turn application, physical-session reuse/rotation invariants, restart/reconnect reconciliation, deadline/abort, and the retirement of the Workflow-path ACP bridge. The bundling is intentional (the guidance merges tightly-coupled changes into one functional unit), but the slice is wide.
  SuggestedAction: If T-004 grows beyond a reviewable size during implementation, consider splitting the Workflow-path ACP retirement into a follow-up slice only if it can be done without leaving dead code; otherwise keep it bundled.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: alignment
  Evidence: `mohist/acp-agent` stays registered in #409 (retained for the AgentJob path). Built-in profiles were already migrated to `mohist/opencode` by #408, but a custom Workflow profile still declaring `uses: mohist/acp-agent` would continue to dispatch through the retained ACP Action rather than failing at dispatch.
  SuggestedAction: Confirm no in-flight custom Workflow profiles depend on Workflow-source ACP semantics; the eventual "fail at dispatch" behavior for legacy `mohist/acp-agent` Workflow tasks is owned by #410 when the Action is removed.
  Status: follow-up

## Coverage Summary

- All 15 issue Acceptance Criteria trace to proposal "What Changes" entries, spec requirements, and tasks.
- All 4 proposal capabilities (`opencode-runtime`, `opencode-turn-execution`, `opencode-session-operations`, `opencode-model-catalog`) have matching spec directories with the exact kebab-case names.
- All 27 spec requirements (7 + 8 + 7 + 5) are covered by tasks; every spec is now explicitly referenced by at least one task's `spec` field.
- All issue Non-Goals are respected by the plan (AgentJob ACP migration, `mohist/agent`, Pi/generic `AgentRuntime`, OpenCode CLI install/lock, permission→Approval mapping, and `client.v2.session.wait/compact` are all excluded).
- The Workflow-only ACP cleanup scope is preserved: `mohist/acp-agent`, the shared ACP connection, the generic/AgentJob session strategy, and `@agentclientprotocol/sdk` are retained until #410 across proposal, specs, design, and tasks.
- `tasks.json` dependency graph is a valid DAG; every non-first task has `dependsOn`, all dependencies point to strictly-lower-priority existing tasks, and no task is an over-fine technical step or a standalone test task.

<promise>PASS</promise>
