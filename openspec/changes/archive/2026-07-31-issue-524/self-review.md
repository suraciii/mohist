# Self-Review: Issue 524 — Slack DM continuous conversation and work control

## Acceptance Criteria Coverage

All 7 acceptance criteria are covered by specs and tasks:

| AC | Spec requirement | Task |
|---|---|---|
| Normal DM continues current session, no new AgentJob | dm-session-continuity: "One current AgentSession per DM conversation" | T-001 |
| New task creates new AgentJob+Session, becomes current | dm-session-continuity: "New task switches the current session without canceling prior work" | T-002 |
| Old work continues; late replies carry work identity | dm-session-continuity: "Late replies from superseded work carry identifiable work identity" | T-002 (continuation) + T-003 (identity) |
| DM during Turn execution accepted and queued, "accepted pending" | dm-session-continuity: "A DM during Turn execution is accepted and queued" | T-001 |
| Cancel queued / stop executing from DM | dm-work-control: "The Owner can cancel or stop work from the DM" | T-004 |
| Expired stop entry does not stop later work | dm-work-control: "An expired control entry does not stop later work" | T-004 |
| Duplicate Slack message produces no second input or work | dm-session-continuity: "A redelivered DM resolves to the same input" | T-001 |

## Spec Format

- All requirements use `### Requirement:` headers; all scenarios use exactly `####` hashtags. ✅
- Normative SHALL/MUST language throughout. ✅
- Every requirement has at least one scenario. ✅
- No ADDED/MODIFIED/REMOVED headers; no cross-spec references. ✅
- Both capabilities from the proposal have corresponding spec files. ✅

## Task Graph

- Valid DAG: T-001 (P1, no deps) → T-002 (P2), T-004 (P3); T-003 (P2, no deps). ✅
- All `dependsOn` reference strictly lower-priority tasks. ✅
- Each task is independently deliverable and includes test coverage in acceptance criteria. ✅
- Tasks are split by feature module, not over-granular technical steps. ✅

## Cross-Artifact Consistency

- Proposal capabilities → spec files: both present. ✅
- Design decisions D1-D7 → all mapped to tasks. ✅
- Design decisions → spec requirements: all covered. ✅
- Issue non-goals respected across proposal, design, and specs. ✅

## Issues Found

### 1. [Moderate] Design D2 short-circuit on `AlreadyExisted` risks breaking crash recovery

**Location:** design.md D2, routing pseudocode and rationale.

The current ingress code (`SlackConnectionRoutes.cs:339-348`) ALWAYS calls `LaunchConnectionAsync` regardless of `AlreadyExisted`, then uses `AlreadyExisted` only to adjust the ack text. This is deliberate: if a crash occurs between `inbox.AcceptAsync` (row insert, `DispatchedAt = null`) and `LaunchConnectionAsync`, the Slack redelivery re-enters, `AcceptAsync` returns `AlreadyExisted = true`, and the always-call launch path drives the work forward (the coordinator is idempotent).

Design D2 proposes short-circuiting on `AlreadyExisted`: "a redelivered message returns the same ack without re-evaluating the mapping or re-launching." In the crash-recovery case (inbox row exists, launch never happened), this short-circuit would skip the launch entirely — the work would never start, violating the issue-514 redelivery guarantee ("a redelivered Slack message resolves to the same SessionInput").

`SlackProviderInboxAcceptResult` returns only `(Id, AlreadyExisted)` — no dispatched state — so the handler cannot distinguish "processed and dispatched" from "accepted but crashed before dispatch."

**Recommended fix:** preserve the current pattern (always call launch/follow-up, which are idempotent) and use `AlreadyExisted` only for ack text, as the current code does. Alternatively, enrich the accept result with dispatched state and short-circuit only when dispatched.

### 2. [Minor] Interface name error in design and tasks

**Location:** design.md D1, tasks.json T-001.

Both reference `ISlackConnectionProviderCleanup`. The actual interface is `IAgentConnectionProviderCleanup` (`Agent/Services/IAgentConnectionProviderCleanup.cs`), implemented by `SlackProviderInboxStore` and `SlackOutboxStore`.

### 3. [Minor] dm-work-control spec scenario wording does not match DM interaction model

**Location:** specs/dm-work-control/spec.md, "Cancel and stop act on a single identified Turn" requirement.

The scenario says "WHEN a cancel or stop is issued from the DM carrying a specific Turn identity." From the DM, the Owner does not provide a Turn identity — the system implicitly resolves the current Turn (design D6). The scenario reads as if the DM caller supplies a Turn ID, contradicting the design. The behavior (only one Turn affected) is correct; only the WHEN clause wording is misleading.

### 4. [Observation] D7 work label source is feasible

Design D7 says the work label is "derived from the first SessionInput." Verified that `AgentJobGrain.State.Input.Prompt` contains the launch prompt (`AgentJobGrain.cs:422, 656`), and `BuildTerminalDeliveryEnvelope` is called from within the job grain (line 1644) where `State.Input` is accessible. No cross-grain session read is needed. The design's description is accurate.

### 5. [Observation] Keyword collision risk is acknowledged

D3 ("new task management system") and D6 ("stop sending emails") collision risks are explicitly called out in Risks and Open Questions. This is a known trade-off accepted for v1, not an oversight.

## Verdict

The plan is comprehensive, well-researched, and covers all acceptance criteria with a sound architecture that reuses existing follow-up and Turn-control machinery. The task graph is a valid DAG with correct dependencies and test coverage in every task.

Issue #1 is a real design concern (crash-recovery regression from the proposed short-circuit), but the fix is trivial and the implementer will see the existing always-launch pattern in the current code. Issues #2 and #3 are minor naming/wording inaccuracies. None prevent the plan from being built.

<promise>PASS</promise>
