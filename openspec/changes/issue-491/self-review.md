# Self-Review — issue-491

Reviewer mode: findings only, no files changed except this one.
Artifacts reviewed: `proposal.md`, `specs/agent-response-failure/spec.md`,
`specs/approval-attribution/spec.md`, `design.md`, `tasks.json`. Cross-checked
against the issue body and the live codebase.

## Acceptance-criteria → spec → task coverage

| Issue acceptance criterion | Spec requirement | Task(s) |
|---|---|---|
| AgentJob 终态失败（含 preflight）后 inbox 出现条目，配置 Hermes 时收到推送 | `agent-response-failure` req 1, 3, 4 | T-001 + T-003 |
| 「Agent 响应失败」通知种类默认开启，可关 | `agent-response-failure` req 3, 4 (disable scenarios) | T-003 |
| 指向失败 Agent 自身的路由规则不响应它自己的 `agent.job.failed` | `agent-response-failure` req 5 | T-001 |
| `mo run approve --author supervisor` 后读取结果含操作者 | `approval-attribution` req 1, 3, 5 | T-002 |

Every acceptance criterion maps to at least one spec requirement and at least
one task. Non-Goals (auto-retry, cooldown, A→B→A loop prevention) are respected
— none is silently pulled into scope.

## Strengths (do not regress)

- **Specs are well-formed.** Every scenario uses exactly four hashtags, normative
  SHALL/MUST language, states target behavior directly with no
  `ADDED/MODIFIED/REMOVED` headers, and every requirement has ≥1 scenario.
- **Task graph is sound.** `tasks.json` is valid JSON; the dependency graph is a
  DAG with the priority invariant holding (sole edge T-003 → T-001, priority
  2 → 1). Each task carries its own test coverage in acceptance criteria; no
  standalone test task; no over-granular technical-step split.
- **Design is grounded in the code.** D1 relies on the no-DbContext
  `IEventStore.AppendAsync(CloudEvent, ct)` (verified to exist,
  `IEventStore.cs:17`) and the existing `agent-job-recovery` reminder pattern;
  D5 copies the comment-author model end-to-end. Alternatives are considered for
  D1, D3, D5.
- **Lineage sourcing is feasible.** `RoutedAgentLaunchPlan` already carries
  `ProjectId`, `IssueNumber`, `EpicNumber`, and `AgentId`
  (`IAgentJobGrain.cs:153-173`), so D2's `agentid` + issue/epic stamping is
  backed by the data model.

## Findings

### F1 — BLOCKER: the BREAKING approve/reject `--author` is not paired with updating the agent command surface

`approval-attribution` spec requirement 2 makes `--author` **required** (missing
→ decision rejected), and `proposal.md` marks this **BREAKING**. Yet the
agent-facing command surface that agents and the supervisor preset use documents
and invokes approve/reject **without** `--author`:

- `packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md:77` — `mo run approve <run-id>` / `mo run approve --issue <number>`
- `packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md:78` — `mo run reject <run-id> --message <m>`
- `packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md:151` — documents `-m` as reject's required flag, no mention of `--author`

`design.md`'s Migration Plan explicitly says the change must "ship together
with the agent/supervisor preset updates that now pass `--author`" — but
**no task in `tasks.json` owns that update.** T-002's scope names the CLI
option, the HTTP DTOs, the grain, and the domain, but not the agent preset /
`skill-data/mohist/SKILL.md`.

Consequence of building as-is: an autonomous build of T-002 lands a required
`--author`, after which every agent (including the supervisor) calling the
documented `mo run approve <run-id>` is **rejected**. Agents can no longer place
approval gates — which defeats the feature's own purpose ("审批历史里能区分人和
agent" requires agents to actually declare an operator), and breaks the
documented command at deploy.

**Fix (for the fixer, not me):** fold the `skill-data/mohist/SKILL.md` approve
and reject command examples (lines 77-78, 151) plus the supervisor preset's
approval instructions into T-002's scope — it is the same feature module (the
approve/reject command surface change), so it belongs with the CLI/API/grain
switchover, not as a separate task. Confirm at build that no other shipped
preset text drives a bare `mo run approve`/`reject`.

### N1 — proposal Impact overstates web scope vs. task graph (non-blocking)

`proposal.md` Impact lists "Web：审批历史展示需呈现 decidedBy（读取模型已携带）",
but no task implements web display, and the issue's acceptance criterion is
read-result-only (the API carrying `decidedBy` satisfies it). As written, the
proposal Impact claims work the task graph does not perform. Reconcile: either
soften the proposal line to mark web display as a follow-up, or add the web
scope. Strict acceptance criteria are still met by T-002 without it.

### N2 — acceptance criterion #1 spans two tasks (acceptable, noted for traceability)

The user-visible notification ("failure → inbox item + Hermes push") is
delivered only by T-001 (event emission) **and** T-003 (notification
projection) together; no single task produces the inbox/Hermes outcome. This is
a defensible module split — emission vs. projection are independent components
and T-003 depends on T-001 — so the end-to-end criterion is satisfied when both
land. Noted for traceability, not a defect.

### N3 — informational: confirm watch/@mention Input retains issue context

Lineage stamping is verified feasible for the routed-launch path
(`RoutedAgentLaunchPlan.IssueNumber`). D2 sources lineage from "State.Input /
State.RoutedPlan". At build, confirm the watch-launch / @mention `Input` also
retains the originating issue id, so those (issue-scoped) failures project into
the inbox rather than being dropped as contextless. Low risk; routed path
confirmed.

## Verdict

F1 is a build-readiness defect: the plan authorizes a BREAKING, required
`--author` but leaves the agent command surface that depends on it unupdated and
unassigned, which an autonomous builder would ship broken. Must be fixed before
building.

<promise>FAIL</promise>
