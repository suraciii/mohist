# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: Design decision #4 had a self-contradiction: first sentence said "If >80%, retry is rejected" but the next sentence said "If 80-90%, a warning is logged but dispatch proceeds" — these contradict because 80-90% is a subset of >80%. The conclusion sentence ("90% hard-block threshold vs 80% warn threshold") clarified the intended behavior.
  What was changed: Rewrote design decision #4 to clearly state the three-tier system: <80% proceed, 80-90% warn+proceed, >90% reject.
  Verification: The decision now reads consistently across all sentences; no contradictory statements remain.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: consistency
  Evidence: `session-recovery/spec.md` and `http-api/spec.md` used endpoint paths `POST /api/issues/:number/coder-sessions/:sessionId/compact` which do not match the existing session route pattern. The existing routes in `IssueRoutes.Sessions.cs` use `sessions/{name}` for detail endpoints. The design correctly specified `POST /api/projects/{ref}/issues/{number}/sessions/{name}/compact`.
  What was changed: Updated all endpoint paths in both `session-recovery/spec.md` and `http-api/spec.md` from `coder-sessions/:sessionId` to `sessions/:name` with the full project-prefixed path.
  Verification: `grep -r "coder-sessions/:sessionId" openspec/changes/issue-110/specs/` returns zero matches.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: consistency
  Evidence: `session-recovery/spec.md` requirement "Retry verifies session health before resuming" stated the exhaustion threshold as "80%" but the scenario used 92% (which is >90%). The `workflow-run/spec.md` had the same issue. The `http-api/spec.md` retry requirement also said "exceeds 80%". The design and `tasks.json` both consistently use 90% as the rejection threshold with 80% as the warning threshold.
  What was changed: Aligned all three spec files (`session-recovery`, `workflow-run`, `http-api`) to the three-tier threshold model: <80% proceed, 80-90% warn+proceed, >90% reject. Changed the `http-api/spec.md` retry scenario from "below 80%" to the concrete value "at 45%" to avoid implying 80% is the rejection boundary.
  Verification: All specs now agree on the same three-tier threshold model. The `tasks.json` T-004 acceptance criteria were already consistent and required no changes.
  Status: resolved

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: completeness
  Evidence: The design notes that the exact contents of the compaction summary are an open question ("Candidate approach: extract the last N user/assistant message pairs that contain task instructions and key decisions, plus session memory insights. This can be refined iteratively."). The specs and tasks treat this as an implementation detail within T-002.
  SuggestedAction: During T-002 implementation, define the summarization algorithm and add a spec scenario for summary content preservation if the algorithm proves complex enough to warrant it.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: feasibility
  Evidence: The `agent-session-ui/spec.md` references a "session page metadata area (model, duration, turn counts, session state)" where context health should appear alongside other metadata. The current `SessionTimeline.tsx` component does not have a distinct metadata area — metadata is embedded in the transcript. T-005 needs to decide whether to create a metadata area or embed health inline.
  SuggestedAction: The T-005 implementer should determine the best visual placement for context health status within the existing session page layout. The spec requirement is satisfied as long as context health is visible on the session page.
  Status: follow-up

<promise>PASS</promise>
