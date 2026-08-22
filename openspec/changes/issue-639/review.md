# Review: issue-639

## Verdict

FAIL — one must-fix problem remains in the Server boundary.

## Must-fix findings

### MF-1 — Unattributed non-activity and mixed batches still pass before a Workflow turn exists

**Where:** `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1703-1724`, in `AppendRuntimeEventsAsync`.

The implementation now recognizes a reconnect batch by requiring every event to be `session.activity` **and** its payload to contain `source: "runner-reconnect"`. However, it only rejects an unattributed non-reconnect batch when `hasWorkflowTurnForRuntime` is already true. It no longer uses the persisted `SourceKind == "workflow"` classification to enforce the pure-activity boundary.

Therefore, for a Workflow-introduced session with the current `runtimeSessionId` but no persisted Workflow turn, an unattributed `message.delta` (and an unattributed mixed `session.activity` + `message.delta` batch) reaches `AppendEventsAsync` and is appended instead of being rejected before append. The same failure occurs for a pure activity batch without the special source field once a Workflow turn is present: it is not treated as the specified activity-only relaxation.

This violates the issue acceptance criterion that `AppendRuntimeEventsAsync` must still reject non-activity events without turn binding on Workflow-introduced sessions, and the capability requirement **“The relaxed path is limited to pure activity batches”** in `openspec/changes/issue-639/specs/session-runtime-activity-reconciliation/spec.md` (especially the scenario for an unattributed non-activity event when no matching persisted turn exists). It also violates the design decision that persisted `SourceKind == "workflow"` is authoritative and that mixed/non-activity unattributed batches are rejected before append.

The boundary must enforce the Workflow-session pure-activity rule independently of whether a Workflow turn has already been persisted, while retaining acceptance for current-binding unattributed activity-only observations. Add regression coverage for both a no-turn unattributed `message.delta` and a no-turn mixed batch, and verify that neither mutates transcript/session state.

## Review dimensions

- **Issue basis: checked, no issue.** The issue acceptance criteria and the plan/spec artifacts were read before judging the implementation.
- **Coverage: FAIL.** The changed boundary tests cover non-activity and mixed batches only after seeding an acknowledged Workflow turn. They do not cover the required no-turn cases, which is where the implementation is incorrect.
- **Correctness: FAIL.** MF-1 leaves the fail-closed Workflow attribution boundary incomplete.
- **Consistency with the surrounding codebase and plan artifacts: FAIL.** The final implementation diverges from `design.md` decision 1 and the session-runtime-activity specification by replacing persisted Workflow source classification with a payload-source special case.
- **Tests: checked, no issue in the executed suites, but insufficient for this criterion.** `npm run verify` passed; the focused Runner suites passed 70 tests, and the Server SpecTests passed 3003 tests. Those results do not establish the missing no-turn rejection behavior because the relevant regression case is absent.

## Observations

- The Runner outbox changes for structured deterministic-refusal classification, three-refusal settlement, two-empty already-consumed settlement, cleanup receipt arrays, retention warning edge-triggering, and saturated-group liveness were covered by the focused tests and showed no additional must-fix issue in this review.
- The full repository verification completed successfully, including Runner typechecking, Server tests, formatting, file-size checks, and architecture checks.

<promise>FAIL</promise>
