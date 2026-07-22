# Self-Review — Issue 452

Reviewer: plan-stage self-review of `openspec/changes/issue-452/` (proposal, specs,
design, tasks) against issue #452. Review only; no files changed except this one.

## What holds up

- **Capability ↔ spec ↔ decision ↔ task mapping is consistent.** The 3 proposal
  capabilities each have a spec file; design decisions D1–D7 map onto them; the 4-task
  DAG covers all of them. Every issue AC traces to at least one task.
- **Load-bearing technical claims verified against code.** `AgentJobInput` skips
  `Id(4)` (uses 0,1,2,3,5,6,7,8,9) and `RoutedAgentLaunchPlan` tops out at `Id(18)`,
  so the design's "next free Id(4)" / "Id(19)" snapshot placement is correct and
  append-only. `AgentSessionInfo.Runtime` is already returned to the runner
  (`RunnerRoutes.cs:478,692`), so D5 (generic open/attach derive runtime from the
  session) is sound.
- **Spec format is compliant.** Every requirement uses `### Requirement` with ≥1
  `#### Scenario` (4 hashtags) in WHEN/THEN form and SHALL/MUST language.
- **Backward compatibility is real** (additive `agentConfig.runtime`; Orleans null
  defaults), and the task graph is a valid DAG with deps pointing only to
  strictly-lower-priority tasks.

## Problems that must be fixed

### P1 — BLOCKER: the issue-level override (AC #2) has no owned write path, and OQ1 is unresolved

AC #2 requires "issue 级覆盖可以改变本次启动使用的后端". The design (D2/OQ1) recommends
T-001 **read** the override from the issue's `vars.agent.runtime` at launch. But:

- No task is responsible for **writing** a per-issue `vars.agent.runtime`. Today the
  Web writes only `{model, variant}` into `vars.agent`
  (`entities/settings/api/client.ts:64,200`), and nothing in T-004's scope or ACs
  asserts it persists a backend choice into issue/stage variables — T-004 only says
  the pickers "list models for the selected backend".
- Consequently the override is always absent, so AC #2 is not demonstrable
  end-to-end. T-001's resolver-level unit test can prove precedence in isolation,
  but there is no UI/API path that actually sets the override for a Mohist Agent
  launch.
- OQ1 is flagged "primary" and unresolved, yet T-001 is AFK and told to "implement
  the recommended source". An autonomous builder cannot confidently satisfy AC #2
  from this plan.

**Fix direction:** either (a) decide OQ1 and add an explicit owner + AC for writing
the issue-level backend override (and make the read/write tasks reference each
other), or (b) re-scope AC #2 to a mechanism the plan actually delivers end-to-end
(e.g. a launch-request field with a clear writer). As written, the plan is not
buildable to "done" for AC #2.

### P2 — `AgentConfigSchema.Filter` has a second hardcoded key list that T-001 does not update

`AgentConfigSchema` has **two** key surfaces: `AllowedKeys` (used by `Validate`) and
a separate hardcoded list inside `Filter` — `foreach (var key in new[] { "model",
"variant" })` (`AgentConfigSchema.cs:88`). `Filter` is the write-side merge path used
by `IssueVariableBuilder`, `MohistIssueWorkflowProfileBase`, and `ConfigService` (per
its own docstring). T-001's AC only names `Validate`/`AllowedKeys`, so `runtime`
would be silently dropped on those merge paths even though it passes validation.

**Fix:** T-001 must update both `AllowedKeys`/`Validate` **and** `Filter` (derive
`Filter` from `AllowedKeys`, or add `runtime` to its list), with an AC covering a
round-trip through `Filter`.

### P3 — T-002 does not address the AgentJob output `kind` hardcoded to `"opencode"`

`buildAgentJobOutput` hardcodes `kind: "opencode"` in the terminal AgentJob output
(`agent-job-executor.ts:250`), consumed by `AgentJobGrain.ReportResultAsync`. A
Pi-executed AgentJob would report `kind: "opencode"`. T-002's description and ACs
cover runtime selection, readiness failure, and fact projection, but never touch the
output `kind`/runtime labeling. At minimum this needs a decision (is `kind`
runtime-specific, and if so who sets it) and an AC; otherwise Pi runs are
mislabeled in the terminal result.

**Fix:** T-002 must account for the output labeling on the Pi path (parameterize
`kind` from the selected runtime, or document why `opencode` is intentional) with a
verifiable AC.

### P4 — Minor: proposal "BREAKING" contradicts design "preserves legacy behavior"

Proposal (`proposal.md:15`) marks the `/opencode/models` change **BREAKING**, but the
design (`design.md:64`) says the generalized endpoint "keeps `opencode` as the
default when `?runtime=` is absent, so any legacy caller behavior is preserved".
These are contradictory. If the route stays `/opencode/models` with an additive
`?runtime=` and an unchanged `{models, modelVariants}` shape, it is additive, not
breaking.

**Fix:** reconcile — either drop the BREAKING claim or specify the actual
route/shape change that makes it breaking.

### P5 — Minor: T-001 is large and entangles the riskiest seam (OQ1 override-read)

T-001 spans D1–D5 plus reading issue variables for the override. Per the splitting
rules merging is defensible (one server feature module), but the override-read is
the single riskiest, least-decided piece (it couples the launch path to
issue-variable reads) and is bundled with the rest. This is not wrong, but it
amplifies P1: until OQ1 is resolved, the largest task carries the most uncertainty.

**Fix:** resolve OQ1 first; if the override-read survives, consider isolating it so
the core backend-routing pipeline can proceed independently.

## Verdict

P1 is a blocker (AC #2 not achievable end-to-end; OQ1 unresolved), and P2/P3 are
concrete implementation gaps with verified code evidence. The plan is not ready to
build as-is.

<promise>FAIL</promise>
