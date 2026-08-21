# Self-Review: issue-619

## Verdict

FAIL. The plan has must-fix gaps relative to the issue's acceptance criteria.

## Must-Fix Findings

### MF-1 — The planned gate can block a non-executable Agent without making the required diagnostic gap visible

**Violates issue acceptance criterion 2:** Owners/authorized operators must be able to see the concrete readiness gap, Connection state, and next action while ordinary callers see only the safe summary.

`design.md` §4 says operators will continue using the existing diagnostic endpoint, and T-002 only requires that endpoint to “still expose concrete facts.” However, the current endpoint (`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:154-165`) derives `agentReadiness` with `AgentReadinessDeriver`, which only reports the structural `ready`/`needs_setup`/`unknown` projection. The planned admission path is explicitly based on `AgentReadinessService` (`design.md:50`, `tasks.json` T-002), whose current result includes execution-history-based `not-executable` states and concrete `AgentExecutabilityGap` entries (`AgentReadinessService.cs:64-71`, `:104-130`). `ConnectionDiagnostic` currently accepts only the simple readiness string, so a newly blocked `not-executable` Agent can still appear diagnostically healthy or lack its concrete gap.

The plan must explicitly update the existing authorized diagnostic path to use/expose the same canonical admission result (including the concrete gap and next action), or otherwise define a durable diagnostic projection that the endpoint serves. Without that, the safe nudge is implemented but the operator repair path required by the issue is incomplete.

### MF-2 — DM classification omits the existing `new task` new-work branch

**Violates the issue's new-work admission goal and acceptance criteria 1 and 7:** new work must be blocked when readiness prevents execution, while only established follow-ups retain their existing behavior.

The current code has an explicit `new task` marker (`SlackConnectionRoutes.cs:1123-1148`). `ResolveInboxRouteDraftAsync` gives that marker `NewTaskLaunch` before consulting the DM session mapping (`:1191-1197`), so a DM with an established Session can intentionally start new work rather than become a follow-up. The plan's classification in `design.md:41-46` instead describes every DM with a current mapping as a follow-up, and T-002 scopes the gate to “a DM without a current Session”; its follow-up-preservation criteria do not exempt the `new task` branch.

If implemented literally, an established DM could send `new task ...` to an Agent that is no longer ready and bypass the new-work gate, or could receive follow-up treatment when it should launch a new task. The plan must classify the explicit new-task marker as new work before applying the gate, while preserving the ordinary established-session follow-up path.

### MF-3 — The test plan does not actually require an end-to-end Server-ingress-plus-adapter test

**Violates issue acceptance criterion 6:** end-to-end tests must run ingress and the adapter together and verify that outbox and direct-send paths do not duplicate.

`design.md` §7 and T-004 describe separate Server integration/spec tests and real Node adapter tests that “share representative JSON fixtures.” Shared JSON fixtures test the wire shape from both sides, but they do not exercise one Server ingress request producing an ownership result, the real adapter event handler consuming that result, and the corresponding outbox/direct-send behavior in one end-to-end flow. No task names an integrated harness or a test that runs both components across the Server-owned and adapter-owned boundary.

The plan must add an explicit end-to-end test task/acceptance criterion covering at least Server-owned durable nudge (no direct post), adapter-owned fallback (one direct post), and the no-duplication boundary through the real ingress and adapter paths.

## Review Dimensions

### Issue basis

Checked, no issue. I read the canonical issue body and the planning-reset comment before interpreting the artifacts. The review basis is the seven issue acceptance criteria, especially the operator diagnostic requirement, single response owner, durable deduplication, legacy direct fallback, and combined ingress/adapter testing.

### Coverage

Must-fix issues found. The artifacts cover the three new-work shapes, durable outbox identity, response ownership, safe public summaries, preservation cases, and uncertain delivery at a high level. They do not fully cover the concrete diagnostic contract for the planned non-executable state (MF-1), the existing DM `new task` route classification (MF-2), or the required end-to-end test boundary (MF-3).

### Correctness

Must-fix issues found. The proposed ownership and outbox approach is directionally consistent with the current stores, but the literal classification described by the plan can admit the wrong DM branch, and the planned canonical readiness check is not connected to the diagnostic facts that operators must see. Separate fixture-based adapter tests cannot prove the actual Server/adapter no-duplication behavior required by the issue.

### Consistency with the current codebase and conventions

Must-fix issues found as described above. The findings are grounded in current code: `AgentReadinessService` and `AgentReadinessDeriver` are distinct readiness surfaces, `ConnectionDiagnostic` currently consumes only the latter's string projection, and DM routing already has a separate `new task` classification. The proposed changes need to account for those existing boundaries rather than treating “current mapping” as synonymous with “follow-up.”

### Task breakdown, ordering, and verifiability

Must-fix issues found. The T-001 → T-002 → T-003 → T-004 ordering is otherwise coherent, but there is no explicit task for wiring canonical non-executable gaps into authorized diagnostics, no task/criterion for the `new task` DM case, and no task requiring a true cross-component end-to-end test. The current acceptance wording is therefore insufficient to verify all issue criteria.

## Observations

- `design.md:148` leaves the exact channel-root reply anchor unresolved. Both a root-thread reply and a top-level message can satisfy the issue's same-conversation/context requirement, so this is a product-UX decision rather than a must-fix planning defect.
- `design.md:149` leaves the complete enabled-Connection availability mapping as an open question. The proposed mapping names setup-incomplete, credential-failure, and service-offline states and preserves owner/identity policy, so the coverage is present, but the final boundary should be settled before implementation.
- `design.md:150` leaves exact caller wording and whether to include a diagnostic link open. The fixed generic-summary rules and forbidden-content tests are sufficient for the issue; wording/link selection is an observation.
- The design retains tolerant decoding for older Server responses despite the repository-wide preference to remove obsolete compatibility paths. This is an explicit migration decision in the plan and does not by itself violate issue 619.
- The legacy adapter-owned direct fallback remains non-durable and therefore retains its existing post/ack uncertainty window. The issue explicitly preserves that no-intent fallback, while the durable exactly-once guarantees apply to the Server-owned nudge path.

<promise>FAIL</promise>
