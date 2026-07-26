# Self-Review — issue-491 (round 2)

Reviewer mode: findings only, no files changed except this one.
This pass re-reviews after the round-1 fixes. Artifacts reviewed:
`proposal.md`, `specs/agent-response-failure/spec.md`,
`specs/approval-attribution/spec.md`, `design.md`, `tasks.json`, checked
against the issue body and the live codebase.

## Round-1 findings — status

- **F1 (was BLOCKER)** — RESOLVED. Investigation during fixing corrected the
  premise: the supervisor preset **already** declares `--author` on
  approve/reject (`packages/cli/Mohist.Cli/presets/supervisor/instructions.md:24`
  — "审批和写 comment 一样要署名：approve / reject 时 --author 声明 supervisor"),
  mirroring its comment `--author`. The genuine residual was only the generic
  command reference (`skill-data/mohist/SKILL.md:77-78`) omitting the now-required
  `--author`. That update is now folded into T-002 (description, a new acceptance
  criterion, and notes), and `design.md`'s Risk #4 + Migration step 1 now state
  the supervisor preset needs no text change. The BREAKING change no longer
  displaces the supervisor at deploy.
- **N1 (web scope)** — RESOLVED. `proposal.md` Impact line softened to a
  non-blocking follow-up; acceptance is read-result-only, which T-002 satisfies.
- **N3 (lineage for watch/@mention)** — RESOLVED. T-001 notes now instruct
  confirming at build that the watch/@mention `Input` retains the originating
  issue id (routed path already carries it via `RoutedAgentLaunchPlan.IssueNumber`).

## Acceptance-criteria → spec → task coverage

| Issue acceptance criterion | Spec requirement | Task(s) |
|---|---|---|
| AgentJob 终态失败（含 preflight）后 inbox 出现条目，配置 Hermes 时收到推送 | `agent-response-failure` req 1, 3, 4 | T-001 + T-003 |
| 「Agent 响应失败」通知种类默认开启，可关 | `agent-response-failure` req 3, 4 (disable scenarios) | T-003 |
| 指向失败 Agent 自身的路由规则不响应它自己的 `agent.job.failed` | `agent-response-failure` req 5 | T-001 |
| `mo run approve --author supervisor` 后读取结果含操作者 | `approval-attribution` req 1, 3, 5 | T-002 |

Every acceptance criterion maps to ≥1 spec requirement and ≥1 task; Non-Goals
(auto-retry, cooldown, A→B→A loop prevention) are respected.

## Strengths

- **Specs well-formed**: every scenario uses exactly four hashtags, normative
  SHALL/MUST language, target behavior with no `ADDED/MODIFIED/REMOVED` headers,
  and every requirement has ≥1 scenario.
- **Task graph sound**: valid JSON; DAG with the priority invariant holding (sole
  edge T-003 → T-001, priority 2 → 1); each task carries its own test coverage;
  no standalone test task; no over-granular split; module boundary (event
  emission vs notification projection) is defensible.
- **Design grounded in code**: D1 relies on the no-DbContext
  `IEventStore.AppendAsync(CloudEvent, ct)` and the existing `agent-job-recovery`
  reminder pattern; D5 copies the comment-author template; D2 lineage is backed
  by `RoutedAgentLaunchPlan` (carries `IssueNumber`/`EpicNumber`/`AgentId`).
- **F1 correction propagated consistently** across tasks.json, proposal.md, and
  design.md — no artifact still claims the supervisor preset needs updating.

## Advisory note (non-blocking)

### A1 — T-003's renderer assumes payload fields T-001 does not explicitly commit to

T-003's description says the renderer branch uses "the payload's
failureReason/failureCategory", and `design.md` D2 mandates carrying those in the
event payload. T-001's acceptance criteria, however, only commit to stamping
`agentid` + business lineage — they do not explicitly require the payload to
include `failureReason`/`failureCategory`. The design is the authority and the
builder reads it, so this does not block building; but for autonomous-execution
clarity, adding a T-001 criterion ("payload carries failureReason/failureCategory
per design D2") would make the T-001 → T-003 contract explicit. Low severity.

## Verdict

All round-1 findings are resolved and verified against the codebase. The plan is
coherent, well-specced, grounded in the actual implementation, and its task graph
is build-ready. The single remaining note (A1) is advisory and does not require a
fix before building.

<promise>PASS</promise>
