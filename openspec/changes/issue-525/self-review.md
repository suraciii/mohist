# Self-Review — issue-525 (从 Web 创建和接管 Slack Connection)

Reviewer stance: reviewer, not fixer. Findings only; a separate task applies fixes.

## Summary

The four artifacts are internally aligned on scope, capability boundaries, and the
task graph. All six issue acceptance criteria are covered by the specs; the task DAG
is a valid acyclic graph with strictly decreasing priorities and test verification on
every task; design decisions are grounded in verified code references
(`App.tsx:74`, `AgentDetailPage.tsx:42-45/584`, `SlackConnectionRoutes.cs:33-64`,
`SlackConnectionApiSpecs.cs`, `Agent.cs`). Non-goals match the issue exactly.

One must-fix defect remains: a spec requirement/scenario asserts behavior that the
codebase cannot satisfy and that the design + tasks deliberately contradict.

## Acceptance-criteria coverage

| Issue acceptance criterion | Covered by |
|---|---|
| Create from Agent detail + Bot identity preview + App creation step | `web-connection-setup`: "exposes a Connections entry…", "Bot identity preview…", "external Create in Slack…" |
| Protected credential input/save, no echo/page-state/log | `web-credential-input`: all four requirements |
| Resumable after close/refresh/device | `web-connection-setup`: "Setup progress is owned by the server…" |
| Service offline / token invalid / Agent not Ready → keep progress + single next step | `web-connection-setup`: "Transient blocking conditions do not lose progress…" |
| Summary highlights one state + one next step; four facts readable | `web-connection-setup`: "The summary highlights one current state…" |
| Same progress in Web and CLI; one side holds on the other | `web-connection-setup`: "The Web and the CLI operate the same Connection…" |
| (Product shape) identity verification + Owner claim | `web-connection-setup`: "The owner claim step generates a one-time code…" |

All criteria have at least one requirement with a scenario; scenario hashtag depth is
correct (4 `#`) across both spec files.

## Findings

### F1 — MUST FIX: spec asserts an unsatisfiable "avatar derived from the Agent"

`specs/web-connection-setup/spec.md`, requirement "Creating a Connection presents a
Bot identity preview derived from the bound Agent", states the preview SHALL present
"name, short description, **and avatar** — derived from the bound Agent", and its
scenario "Identity preview is derived from the Agent" asserts the Web shows "the Bot
name, App description, **and avatar** … all derived from the Agent."

This cannot be implemented as written: the Agent carries no avatar field
(`packages/server/src/Mohist.Server/Agent/Domain/Agent.cs` has only Name/Description;
`packages/web/src/entities/agent/api/client.ts` `AgentInfo` has only name/description).
There is nothing to derive an avatar from.

It also conflicts directly with the rest of the plan:
- `design.md` Decision C: "Avatar is deliberately not derived — the Agent carries no
  avatar … the avatar is applied manually in Slack App settings."
- `tasks.json` T-001 acceptance: derives `botName` + `appDescription` only; explicitly
  "Avatar is not derived."
- Product spec `docs/agent-connections.md:61`: "头像需要在 Slack App 设置中手动应用"
  (avatar is applied manually in Slack App settings).

Why it blocks: the spec is the normative contract tasks reference (T-001's `spec`
anchor points at this requirement). A test author encoding the scenario would write an
unwritable/failing test ("show avatar derived from the Agent"). The requirement and its
scenario must be reworded so the preview covers name + description (derived from the
Agent) and the avatar is configured in Slack, not derived — matching design Decision C,
T-001, and the product spec.

Suggested fix scope (for the fix task): in that one requirement + its first scenario,
drop "avatar" from the derived-from-Agent clause and state the avatar is applied in
Slack App settings (not derived). No change to design, tasks, or proposal needed — they
already state the correct behavior.

### O1 — Observation (non-blocking): T-004 does not anchor the claim-owner spec

T-004 implements the owner-claim step and its acceptance criteria cover it ("claim-owner
shows the code once… regenerate re-POSTs and prior code invalidated"), but its `spec`
anchor points only at "Setup progress is owned by the server…". The requirement "The
owner claim step generates a one-time code claimed through the Bot" is satisfied without
being anchored. Not a defect (one anchor per task matches the #517 precedent), but the
fix task may add a cross-reference in T-004 notes for traceability.

### O2 — Observation (non-blocking): "show preview, then navigate" UX tension

Design Decision A / T-003 say Add Slack shows the derived identity preview, then
navigates to the connection page. Showing a preview immediately before navigating away
risks a flash or the user missing it. Not a correctness issue (the connection page can
also surface the preview), but worth a note for the implementer.

## Verdict

One must-fix spec defect (F1): a normative requirement and scenario assert an
unsatisfiable avatar-from-Agent behavior that contradicts the design, the task, and the
product spec. The rest of the plan is consistent and buildable.

<promise>FAIL</promise>
