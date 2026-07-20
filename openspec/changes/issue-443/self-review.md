# Self-Review - issue-443 plan

Reviewed `proposal.md`, `specs/action-output/spec.md`, `design.md`, and
`tasks.json` against issue #443 and the current output call paths.

## Verdict

The plan is not ready to build. The core object-or-null direction and the
runner/server type boundary are sound, but the artifacts contain two direct
contract/task contradictions and leave one existing Workflow consumer without
a specified post-change behavior.

## Findings

### 1. Blocking: the spec promises declared output capture while the design and tasks deliberately leave it disconnected

The proposal includes "declared output capture" in both What Changes and the
`action-output` capability (`proposal.md:11,17`). The spec makes that a
normative, user-observable scenario: a task declares a capture from
`output.prNumber`, and the captured number must result
(`specs/action-output/spec.md:73-88`).

That scenario cannot occur in the current product. `WorkDispatch` has no
outputs declaration, `ServerConnection.toWorkItem` never populates
`RenderedWorkItem.outputs`, and `ServerConnection.report` never sends
`capturedOutputs`. The design acknowledges this and says the path remains
"non-authoritative" and is not wired (`design.md:61`); `tasks.json` repeats
"Do not wire the currently disconnected capturedOutputs field into the
server" (`tasks.json:31`). Merely changing `captureOutputs` to stop parsing a
string cannot satisfy the spec scenario because no real task can reach it.

This also conflicts with the issue's explicit non-goal of not introducing
output declarations/schema. The repair must choose one contract consistently:
remove declared output capture from the proposal capability, spec requirement,
design consumer list, and task acceptance criteria, or explicitly expand the
issue to wire a usable declaration/report path. Given the issue scope, removal
is the bounded fix.

### 2. Blocking: T-001 is not independently deliverable because it breaks the existing Web consumer before T-002

`T-001` changes `TaskStatusView.Output` and the status/timeline API from a JSON
string to a nested object (`tasks.json:20,28`). `T-002`, which depends on it,
then updates the Web model and parsers (`tasks.json:34-54`). This violates the
task-generation requirement that each delivered feature unit be usable and
that tightly coupled interface/call-site switchovers stay together.

With only T-001 delivered, both current Web paths discard the new object:
`parseTimelineTaskOutput` and `TaskProgressPanel.parseTaskOutput` return `null`
for every non-string input. Task output therefore disappears until T-002,
directly violating the issue acceptance criterion that task-detail behavior
remain unchanged. The design also says server, runner, and Web must deploy as
one release (`design.md:7,101-107`), confirming this is an atomic contract
switch rather than two independently deployable features.

The task graph must either merge T-002 into T-001 or move the task-status API
change out of T-001 and into a server+Web vertical slice in T-002 while T-001
preserves the old API shape. The current graph is a valid DAG syntactically,
but not a valid deliverable decomposition.

### 3. Blocking: approval-feedback resolution loses its summary source without a specified replacement or regression decision

Today `WorkflowRun.ResolveFeedback` accepts the raw string report output and
uses it as the fallback `ResolutionSummary`; server specs assert that behavior.
The design changes `TaskReport.Output` to `JsonElement?`, rejects string task
reports, and states that a new object output supplies no generic summary
(`design.md:67-71`). Neither the proposal nor the capability spec declares this
user-visible removal, and `tasks.json` has no approval-feedback regression
criterion or explicit adaptation rule.

This is not a mechanical type change: feedback still resolves, but its visible
summary can silently become `null`. The issue says output-field semantics for
all Actions except `core/process` remain unchanged and asks for existing
built-in profile flows to regress cleanly. The plan must decide and specify
the source of `ResolutionSummary` under object output (for example, an
existing Action-specific field/private fact if one is authoritative), or
explicitly declare and justify removal of summaries. The implementation task
then needs focused server coverage for completed feedback tasks with object
and null output.

## Checks That Pass

- The proposal defines one capability and the matching
  `specs/action-output/spec.md` exists.
- Every requirement has at least one four-hash WHEN/THEN scenario and no delta
  headers.
- `tasks.json` is valid JSON; IDs are unique; `passes` starts false; its one
  dependency references an existing lower-priority task, so the graph is a
  DAG.
- The issue's primary paths are otherwise represented: `core/process`
  `{stdout, exitCode}`, object-or-null validation, atomic missing-path
  `setVars` failure, task-output references, recovery matching, persistence,
  checks/AgentJob isolation, and task-detail rendering.

<promise>FAIL</promise>
