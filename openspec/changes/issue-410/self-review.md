# Self-Review — Issue #410 Plan

Reviewer: GLM-5.2 (plan stage self-review)
Artifacts reviewed: `proposal.md`, `design.md`, `tasks.json`, `specs/{agent-job-execution,agent-launch-unification,issue-agent-config,acp-removal}/spec.md` under `openspec/changes/issue-410/`.
Issue acceptance criteria: 10. Issue Non-Goals: 5.

## Summary

The plan is comprehensive, internally consistent, and ready to build. All 10 acceptance criteria of the issue map to spec requirements and to specific task acceptance criteria. All 5 Non-Goals are respected by the design and tasks. The 4 capabilities named in the proposal each have a corresponding spec file with well-formed requirements (4-hashtag scenarios, SHALL/MUST language, no delta headers, no cross-spec references). The task graph is a 5-node DAG with priorities and dependencies correctly ordered. Findings below are minor tightening opportunities, not blockers.

## Acceptance Criteria coverage

| AC | Spec coverage | Task coverage | Verdict |
|---|---|---|---|
| AC1 (manual launch → AgentJob + AgentSession + OpenCode) | `agent-job-execution#An AgentJob drives the shared OpenCodeRuntime directly...`, `agent-launch-unification#Manual and event-subscription launches share a single launch pipeline` | T-001 ("A manual `mo agent session launch`...produces a Completed AgentJob whose execution drove `OpenCodeRuntime.runTurn` directly") | ✓ |
| AC2 (subscription launch, bidirectional traceability) | `agent-launch-unification#Event, subscription, AgentJob, and AgentSession are bidirectionally traceable`, `#Subscription arbitration picks one winner...` | T-001 ("An event-subscription launch...drives the same runTurn path") | ✓ |
| AC3 (launch-time-fixed snapshot; edits/archive don't change in-flight) | `agent-job-execution#The AgentJob execution request is fixed at launch time`, `agent-launch-unification#The launch captures a launch-time-fixed Agent snapshot` | T-001 ("Editing the Agent definition's instructions/model/variant...does not change the running turn's inputs"; "Archiving the Agent definition...does not cancel or fail the job") | ✓ |
| AC4 (AgentJob is sole terminal authority) | `agent-job-execution#The AgentJob is the sole authority over its own terminal state` | T-001 ("`AgentJobGrain.ReportResultAsync` transitions `Running → Completed`...; the AgentSession's transcript/usage/lineage events do not transition the job") | ✓ |
| AC5 (independence from Workflow Action contract) | `agent-job-execution#AgentJob execution is independent of the Workflow Action contract` | T-001 (description: "branches on `work.ownerKind === 'agent-job'` BEFORE Action resolution"; "no `mohist/opencode` Action contract") | ✓ |
| AC6 (Follow-up/Compact/Reset/Cancel over unified AgentSession) | `agent-job-execution#Each AgentSession runs at most one work-initiated prompt at a time`; Compact/Reset semantics carried by #407 (referenced in design D3) | T-003 (Follow-up/Cancel handler rework); Compact/Reset preserved from #407 unchanged | ✓ |
| AC7 (issue-level agentConfig converges; no legacy key emission) | `issue-agent-config` (all 5 requirements) | T-002 (11 acceptance criteria covering reject paths, read-back filter, project-layer writers, agent-definition blob) | ✓ |
| AC8 (legacy ACP-bound sessions fail with Reset hint; no data rewrite) | `acp-removal#Legacy ACP-bound AgentSessions fail Session operations with a Reset hint` | Behavior already implemented by #407 (`IsRuntimeRegistered` + `RuntimeSessionMissingException`); T-004 owns the spec but has no explicit acceptance criterion for it | ◑ See Finding F1 |
| AC9 (pre-cutover WorkflowRuns fail with actionable rerun-recoverable error) | `acp-removal#Pre-cutover WorkflowRuns fail subsequent agent task dispatch...`, `#Custom profiles naming the removed Action fail at load or dispatch` | T-004 ("A pre-cutover WorkflowRun dispatching a task whose persisted uses is `mohist/acp-agent` fails with a named, actionable error..."; "A custom profile naming `uses: mohist/acp-agent` fails at profile load") | ✓ |
| AC10 (no ACP anywhere) | `acp-removal` (all 10 requirements) | T-004 (19 acceptance criteria covering code deletion, dependency removal, server/web literal sweeps) | ✓ |

## Non-Goals compliance

| Non-Goal | Where respected |
|---|---|
| No `mohist/agent` Action | Design D1 explicitly rejects this alternative; T-001 introduces an owner-kind branch above Action resolution instead |
| No Workflow task reuse of predefined Mohist Agent config | Design and tasks introduce no such coupling; the AgentJob path and Workflow Action adapter share only the runtime backend |
| No subscription filter/priority/coordination redesign | `agent-launch-unification` spec restates the existing arbitration semantics verbatim ("group by Agent, pick highest priority within group, tie-broken by lex-smaller subscription id") |
| No new AgentTask/AgentThread/Session model | Design preserves `AgentJob` + `AgentSession`; no new aggregate is introduced |
| No Pi or other execution backend | Design uses only `OpenCodeRuntime` from #409 |

## Internal consistency

- **Proposal → Specs**: 4 capabilities named in `proposal.md` → 4 corresponding spec files; capability descriptions match the spec requirement sets.
- **Specs → Design**: D1↔agent-job-execution, D2↔agent-job-execution (independence), D3↔acp-removal (handlers) + agent-job-execution (Follow-up), D4↔acp-removal (readiness gate), D5↔issue-agent-config (all), D6↔acp-removal (legacy/pre-cutover/custom profile), D7↔design test plan.
- **Design → Tasks**: T-001↔D1+D2, T-002↔D5, T-003↔D3, T-004↔D4+D6+deletion steps, T-005↔migration step 10.
- **Task graph**: 5-node DAG; priorities 1–5 strictly increasing along every `dependsOn` edge. T-002 (config convergence) is correctly modelled as parallel to T-001 (no dependency); T-003 depends only on T-001 (not T-002); T-004 depends on T-001+T-003; T-005 depends on T-004.
- **Spec form**: every scenario uses exactly 4 hashtags; every requirement has ≥1 scenario; no `## ADDED/MODIFIED/REMOVED` headers; no cross-spec references; SHALL/MUST language throughout.
- **Tasks form**: every task is a vertical slice (interface + implementation + call-site switchover + tests folded in); no standalone test tasks; no over-granular technical steps; `passes: false` on all.

## Findings

### F1 (minor): T-004 missing explicit acceptance for legacy ACP-bound session Reset hint

`acp-removal#Legacy ACP-bound AgentSessions fail Session operations with a Reset hint` is the only one of the 10 acp-removal spec requirements without a matching acceptance criterion in T-004. The behavior itself is already implemented by #407 (`AgentSessionGrain.IsRuntimeRegistered` only accepts `"opencode"`; any legacy `runtime: "acp"` binding surfaces as `RuntimeSessionMissingException` with the Reset hint), so this is a verification gap, not a capability gap. The implementer can either add an acceptance criterion such as *"A Session command against an AgentSession whose persisted runtime binding is not `opencode` fails with `RuntimeSessionMissingException` and the existing Reset hint wording; the session, transcript, and lineage remain queryable"* or cite the #407 spec test as covering it.

### F2 (minor): T-001 acceptance does not explicitly pin AgentSession open-time invariants

The launch pipeline's `AgentSession` open behavior (`runtime: "opencode"`, fresh vs deterministic session id, trigger labels) is already correct today and `agent-launch-unification#The AgentSession is opened at launch with the canonical source shape` requires it to remain so. T-001's acceptance criteria focus on the new runner execution path and don't explicitly assert the open call shape is preserved. Since T-001 is the task that touches `AgentLauncher.cs`, a regression criterion such as *"An AgentJob launch opens the AgentSession with `runtime: 'opencode'`, source kind `agent-launch`, and the existing source/trigger labels; the open call shape is unchanged from pre-T-001"* would make the regression guard explicit. Low risk because the design doesn't propose changing the open call.

### F3 (minor): Compact/Reset semantics are referenced but not restated

AC6 ("Named Agent 的 transcript、Follow-up、Compact、Reset 和 Cancel 都继续使用统一 AgentSession 产品模型") is partly covered by `agent-job-execution#Each AgentSession runs at most one work-initiated prompt at a time` and the T-003 Follow-up/Cancel rework. The Compact-keeps-binding and Reset-expected-binding-guard semantics are described in #407's archived `opencode-session-operations` spec and referenced in design D3, but the new specs don't restate them. This is acceptable (the #407 contract is the spec of record and is preserved), but a one-line note in T-003 or in the proposal's capability text pointing at the #407 archived spec would improve traceability for a reader who lands on this change cold.

### F4 (informational): Open question on `OpenCodeRuntime.followup`/`cancel` public surface

Design Open Question 1 ("Does `OpenCodeRuntime` already expose Follow-up/Cancel as public methods after #409?") is correctly handled by T-003's "Promote...if not already public" language. No plan change needed; flagged here so the implementer remembers to verify the #409 runtime surface first.

## Verdict

The plan is ready to build. Findings F1–F3 are minor tightening opportunities (adding 1–2 acceptance criteria or a cross-reference); none of them block implementation, and the spec contract is correct in every case. F4 is an open implementation detail that the design and tasks already accommodate.

<promise>PASS</promise>
