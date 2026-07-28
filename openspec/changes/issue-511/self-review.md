# Self-Review — issue-511 (mechanical-debt cleanup), round 2

Reviewer mode: read-only. This file is the only artifact modified. A separate task fixes every problem reported here.

## Verdict

Round 1's findings (task↔spec contract for Group E, web-union mapping, `CheckRunStatus` naming, counts) were addressed and verify clean: `tasks.json` is valid JSON with a sound DAG (only edge `T-005 → T-004`), T-006 carries an honestly empty `spec` field, the four capabilities still map 1:1 to spec directories, every requirement has ≥1 `#### Scenario:` (4 hashtags, zero malformed 3-hashtag scenarios), and `StageCheckStatus` is used consistently. However, this round surfaces two substantive **spec↔design contradictions** (plus a proposal wording clash and a stale count) that were tolerated by the round-1 fixes and will mislead a builder.

## Findings (must fix)

### F1 — Test-rewire mechanism: spec/proposal mandate "test cluster", design/task choose "fake IGrainFactory via proxy"

The spec, proposal, and design/task disagree on *how* the former `BindProfileForTest` consumer is rewired:

- **Spec** (`workflow-grain-production-contract/spec.md:46`): "the test MUST register a fake `IWorkflowProfileReferenceCoordinatorGrain` **in the test cluster**".
- **Proposal** (`proposal.md:8` and `:29`): "switches to registering a fake coordinator grain **in the test cluster**" / "the fake grain registration hook **in the test cluster**".
- **Design D1** (`design.md:40-42`) and **T-001** (`tasks.json:9,24`): chosen approach is "a **fake `IGrainFactory`** that returns a stub coordinator, wired through an extended `GrainTestContext`" (GrainRuntimeProxy); the `InProcessTestCluster` route is "Alternative A … **Rejected as the primary path**".

These are different mechanisms. Taken literally, the spec scenario **fails** if the design's chosen proxy-factory approach is implemented, because no grain is registered in any test cluster. The design's own Open Question (`design.md:116`) claims "the spec contract is identical either way; only the test mechanics differ" — but that is not true while the spec text pins the mechanism to "in the test cluster". A builder cannot satisfy the spec and follow the design simultaneously.

**Resolution direction (for the fix task):** make the spec/proposal mechanism-neutral — the requirement is "no production override hook; the test obtains its `Applied` binding via a fake coordinator (either registered in the test cluster OR returned by a fake `IGrainFactory` in a manual-grain context)" — and let the design pick the proxy-factory approach. Do not mandate "test cluster" in normative spec text.

### F2 — Exhaustiveness: spec/proposal assert a compile error that C# does not provide

The spec and proposal claim compile-time exhaustiveness; the design and task acknowledge the compiler does not provide it:

- **Spec** (`status-wire-mapping/spec.md:18`): "Adding a new value … without adding its mapping **MUST be a compile error**"; scenario (`:23`): "the **build MUST fail** with a non-exhaustive switch error".
- **Proposal** (`proposal.md:13`): "Adding a new enum value without a mapping is now **a compile error**, not a silent `inprogress`".
- **Design D3** (`design.md:59`) and **Risk** (`:94`): "C# switch expressions are **non-exhaustive on enums by default**, so the gatekeeper is a per-enum spec test using `Enum.GetValues`, plus optionally a `_ => throw new SwitchExpressionException` arm".
- **T-003** acceptance criterion (`tasks.json:54`) is honest: "either fails the per-enum `Enum.GetValues` spec or hits the `SwitchExpressionException` at runtime".

So the spec and proposal assert false language behavior. A builder who adds an enum value will see **neither** a build failure nor, necessarily, a silent wrong token — they will get a test failure or a runtime `SwitchExpressionException`. The spec's "MUST be a compile error" / "build MUST fail" is unachievable as written and directly contradicts the design's stated mitigation.

**Resolution direction:** rewrite the spec requirement/scenario to state that exhaustiveness is enforced by the per-enum `Enum.GetValues` test plus a `_ => throw SwitchExpressionException` defense-in-depth arm (i.e. an omission is caught by a **test**, not the compiler), and fix the matching "compile error" sentence in the proposal. The task is already correct; only the spec/proposal over-promise.

### F3 — Proposal contradicts itself on whether web union values change

- `proposal.md:13` says web `WorkflowStageRunStatus` "is reconciled as part of this change" (it gains `completed`).
- `proposal.md:32` (Impact, Web) says the unions "gain a server-authoritative-source comment; **values unchanged** (verified by typecheck + test)".

Adding `completed` to the union is a change to its permitted value set, so "values unchanged" is now false. (Note: the `:17` "literal wire-format status values all stay byte-identical" claim is fine — the server already emits `completed`; only the `:32` "values unchanged" line is wrong.) **Resolution direction:** reword `:32` to "no existing union wire value is removed; `completed` is added to `WorkflowStageRunStatus`".

## Minor observations

- **M1 — stale count in design.** `design.md:98` still reads "14 files / 32 refs" while the proposal was corrected to "~33 occurrences" (actual: 33 occurrences across 14 files in `packages/server`). Trivial, but the two should agree.
- **M2 — carried over, still non-blocking.** T-002 has no `dependsOn` on T-001 although both edit `WorkflowGrain.cs` and `WorkflowProfileManager.cs`. Per the `dependsOn` rule (consumes prior output) this is correctly empty; AFK priority ordering handles the file overlap. Noting only so it is a conscious choice, not an oversight.

## What is correct and need not change

- Capability→spec-directory mapping is 1:1; names match exactly.
- Spec format: all requirements use `### Requirement:`; all scenarios use exactly `#### Scenario:` with WHEN/THEN; every requirement has ≥1 scenario; normative SHALL/MUST throughout; no `## ADDED/MODIFIED/REMOVED` headers; zero malformed 3-hashtag scenarios.
- `tasks.json` is valid JSON; all tasks have every required field; DAG is acyclic; the sole dependency (`T-005 → T-004`) is a true output dependency pointing to a strictly lower priority.
- T-006's empty `spec` field is honest and explained; the `ResolveLayeredVariablesAsync` inline is now backed by the "No pass-through wrapper" requirement in `workflow-run-variables-store/spec.md`.
- The web-union→enum mapping is now pinned, and the `WorkflowStageRunStatus`/`completed` reconciliation is consistently reflected in spec, design D3, and T-003's acceptance criteria.
- `StageCheckStatus` is used consistently; the only `CheckRunStatus` mentions are deliberate "(the issue body's … is a misnomer)" explanatory notes.
- Design decisions cite verified code locations (dead `On` switch at `WorkflowGrain.cs:644-667`, `BindProfileForTest` at `:60`, the `Contains("no current definition")` match at `:624-626`, the embedded `ServerSources/` plumbing + `Microsoft.CodeAnalysis.CSharp` reference enabling the comment-ban ArchTest, and the three `StageRunStatus.Completed` producers at `WorkflowRun.Approval.cs:116` / `WorkflowRun.Stage.cs:50,146`).

<promise>FAIL</promise>
