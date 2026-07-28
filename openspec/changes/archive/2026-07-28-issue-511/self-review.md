# Self-Review — issue-511 (mechanical-debt cleanup), round 3

Reviewer mode: read-only. This file is the only artifact modified. A separate task fixes any problem reported here.

## Verdict

Round 3 finds the plan ready to build. Rounds 1 and 2 surfaced and fixed: the task↔spec contract for Group E, the web-union→enum mapping, the `CheckRunStatus` misnomer, accurate reference counts, the test-rewire mechanism contradiction, and the false compile-time-exhaustiveness claim. This round verified those fixes landed and checked the contract surfaces that previous rounds had not fully exercised — every task `spec` anchor now resolves to a real `### Requirement:` heading, and the capability↔spec-directory mapping is exactly 1:1.

## Verification performed (all clean)

- **Capability ↔ spec directory:** the four capabilities in `proposal.md` (`workflow-grain-production-contract`, `workflow-run-variables-store`, `status-wire-mapping`, `comment-reference-ban`) match the four directories under `specs/` exactly.
- **Task `spec` anchors resolve:** T-001→`no-dead-event-dispatch-path-…`, T-002→`run-scoped-variables-persistence-entry-named-for-what-it-does`, T-003→`wire-format-status-mapping-is-typed-per-enum-not-string`, T-004→`ArchTest forbids issue/spec/design references…`, T-005→`Existing violations cleared to zero, then hard ban` all match real requirement headings (GitHub-style slug); T-006 is intentionally empty (Backoff has no capability home, documented in its notes).
- **Spec format:** every requirement uses `### Requirement:`; every scenario uses exactly `#### Scenario:` with WHEN/THEN; every requirement has ≥1 scenario; normative SHALL/MUST throughout; no `## ADDED/MODIFIED/REMOVED` headers; zero malformed 3-hashtag scenarios. Counts: comment-reference-ban 3/10, status-wire-mapping 5/11, workflow-grain-production-contract 3/10, workflow-run-variables-store 4/9.
- **`tasks.json`:** valid JSON; all six tasks carry every required field; DAG acyclic; the sole dependency (`T-005 → T-004`) is a true output dependency on a strictly lower priority; `passes: false` throughout.
- **Round-2 contradictions resolved:**
  - F1 (test-rewire mechanism): spec scenario and proposal now read "a fake coordinator (either registered in the test cluster, or returned by a fake `IGrainFactory` in a manual-grain context)" — mechanism-neutral; design D1 keeps the proxy-factory approach as primary, and the design Open Question's "the spec contract is identical either way" is now literally true.
  - F2 (exhaustiveness): no "compile error"/"build MUST fail" remains in spec or proposal; both now state exhaustiveness is enforced by the per-enum `Enum.GetValues` test plus a defensive `_ => throw SwitchExpressionException` arm, explicitly noting C# does not compile-check enum switch exhaustiveness. The task (T-003) and design D3 agree.
  - F3 (web union values): proposal `:32` now reads "no existing union wire value is removed, and `completed` is added to `WorkflowStageRunStatus`", consistent with `:13`.
  - M1 (count): design `:98` now "~33 occurrences", matching the proposal.
- **Term consistency:** `StageCheckStatus` used throughout (the only `CheckRunStatus` mentions are deliberate "(the issue body's … is a misnomer)" notes); `WireStatus` rename consistent; `WorkflowStageRunStatus`/`completed` reconciliation consistently reflected in spec, design D3, and T-003's acceptance criteria; the three `StageRunStatus.Completed` producers cited in T-003 (`WorkflowRun.Approval.cs:116`, `WorkflowRun.Stage.cs:50,146`) were verified against the code.

## Minor observations (non-blocking, on record only)

- **The `_ => throw` arm: "defensive" (spec) vs "optionally" (design).** The spec body calls the `SwitchExpressionException` arm "defensive" and a runtime scenario presupposes it exists; design D3 calls it "optionally". This is not a contradiction in outcome — C# switch expressions already throw `SwitchExpressionException` on no-match by default, so the explicit arm is genuinely optional, and T-003 chooses to add it (satisfying both). A builder cannot go wrong here; flagged only so the wording drift is conscious.
- **T-002 has no `dependsOn` on T-001** despite both editing `WorkflowGrain.cs` and `WorkflowProfileManager.cs`. Per the `dependsOn` rule (consumes prior output) this is correctly empty; AFK priority ordering handles the file overlap. Carried forward from round 1; still non-blocking.

These two do not require changes before building.

## What is correct and need not change

- Design decisions cite verified code locations throughout (dead `On` switch at `WorkflowGrain.cs:644-667`, `BindProfileForTest` at `:60`, the `Contains("no current definition")` match at `:624-626`, the already-embedded `ServerSources/` plumbing + `Microsoft.CodeAnalysis.CSharp` reference enabling the comment-ban ArchTest, and the three `StageRunStatus.Completed` producers).
- The two-phase comment-ban split (T-004 establish ratchet with frozen 38-entry baseline, T-005 clear to hard ban) remains well-motivated and matches the design's commit plan.
- Group E is honestly handled: `ResolveLayeredVariablesAsync` inline is backed by the "No pass-through wrapper in variable resolution" requirement in `workflow-run-variables-store/spec.md`; `Backoff` carries an empty `spec` field with a clear justification, and its acceptance criteria are the verification contract.

<promise>PASS</promise>
