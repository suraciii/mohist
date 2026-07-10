---
name: mohist
description: 执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。本 skill 是入口调度器：先判断场景，再加载对应的专题 skill；issue 与 epic 的完整生命周期命令面也由本 skill 承担（不另开专题 skill）。旧 Node CLI 已移除。
---

# mohist

Use this skill for current Mohist .NET backend, API, Web UI, and workflow operations.

Current execution paths:

- Server: `dotnet run --project packages/server/src/Mohist.Server/Mohist.Server.csproj`
- Tests: `dotnet test Mohist.sln`
- Web UI: `npm run dev:web`
- Runner: `npm run dev:runner`

Prefer the current `mo` CLI, ASP.NET Core API, or Web UI workflows. Do not instruct the user to use the removed pre-Orleans Node CLI.

When operating on Mohist issues or workflows:

- Use the current `mo` CLI command surface and current .NET backend behavior as the source of truth.
- When the user provides an existing issue context, load its current state and history via `mo issue show <number>` and related read-only commands, rather than assuming any particular filesystem layout.
- Keep changes scoped to the current issue; do not substitute adjacent cleanup or legacy behavior unless the issue explicitly requires it.
- For local verification, prefer the smallest relevant command or test filter instead of broad full-repo runs.

Boundaries:

- Do not rely on the removed pre-Orleans Node CLI or its workflow/runtime behavior.
- Do not assume Mohist server APIs must be running for purely local CLI or filesystem tasks unless the task explicitly requires server interaction.
- Do not mutate internal runtime data under `.mohist/skills` when the task is about shared coder-agent skills.

## Load the right scenario skill

This skill is the entry point and dispatcher. Judge what the task actually needs,
then load the matching scenario skill for the detailed mechanics:

- **Explore / distill a requirement** (and decide issue vs epic) → `mo skills get mohist-explore`
- **Create an issue** (frontmatter, workflow/risk, labeling, confirmation) → `mo skills get mohist-create-issue`
- **Create an epic** (milestone description, link issues, prerequisites, lifecycle) → `mo skills get mohist-create-epic`

The issue/epic lifecycle commands below (start, approve, reject, retry, rerun,
stop, force-stop, resume, rebase, close, and the epic autopilot trio) are **not**
covered by a dedicated scenario skill — they are reference-level operations
fully answerable by `mo <cmd> --help`. They live here so the dispatcher is the
single source of truth for day-to-day driving of issues and epics, and an
agent does not have to guess, omit, or invent a command.

### Sibling skills

| Skill | When |
|---|---|
| `mohist-explore` | Distill a fuzzy idea into a bounded PRD; decide issue/epic and scope. |
| `mohist-create-issue` | Execute issue creation mechanics. |
| `mohist-create-epic` | Execute epic creation mechanics + autopilot lifecycle semantics. |

## Issue lifecycle commands

These are the state-changing entry points for driving an issue through its
full lifecycle. All take `<number>` (issue number in the active project, or use
`--project` / `--project-id` to target another project).

| Operation | CLI | Effect |
|---|---|---|
| Start | `mo issue start <number>` | Drive the issue into the workflow (Draft → Plan). |
| Approve | `mo issue approve <number>` | Approve at an approval gate (Plan → Build, Check → Integrate). |
| Reject | `mo issue reject <number> --message <m>` | Reject at an approval gate with a change request. |
| Retry | `mo issue retry <number>` | Re-run the current stage after a recoverable failure. |
| Rerun | `mo issue rerun <number>` | Re-run from the beginning of the workflow with fresh attempts. |
| Rerun from stage | `mo issue rerun-from-stage <number> --stage <stage>` | Invalidate the target stage and everything after it; create new attempts from that stage. |
| Stop | `mo issue stop <number>` | **Terminal** stop — cannot be resumed. Use only when you intend to abandon the run. |
| Force-stop | `mo issue force-stop <number>` | Hard-kill the in-flight agent; recoverable with `resume`. |
| Resume | `mo issue resume <number>` | Continue from a paused state. |
| Rebase | `mo issue rebase <number> [--base-branch <b>]` | Rebase the issue branch onto its base. |
| Close | `mo issue close <number>` | Close a completed/abandoned issue. |
| Reopen | `mo issue reopen <number>` | Reopen a closed issue. |

Key distinctions:

- **`stop` vs `force-stop`**: `stop` is terminal (the workflow run ends permanently); `force-stop` is a soft kill — the run is paused and `mo issue resume` brings it back. Choose `stop` only when you mean to abandon.
- **`retry` vs `rerun` vs `rerun-from-stage`**: `retry` re-runs the current stage; `rerun` re-runs the whole workflow from the beginning; `rerun-from-stage` invalidates one named stage and everything after it.
- **`reject` vs `stop`**: `reject` bounces back at an approval gate with a change request (the issue stays alive for another pass); `stop` ends the run.

Read-only and aux helpers (also useful while driving):

```bash
mo issue show <number>            # 详情 + 当前 stage/health
mo issue events <number>          # 事件流
mo issue logs <number>            # 日志
mo issue diff <number>            # 当前分支 vs base 的 diff
mo issue commits <number>         # issue 分支上的 commit
mo issue sessions <number>        # 列出 coder session（plan/build/check/integrate…）
mo issue session transcript <number> <name>   # 某个 session 的对话
mo issue session followup <number> <name> --text <t>  # 向运行中 session 推后续指令
mo issue comment add <number> --body <text>   # 加评论
mo issue prereq add <number> <prereq>         # 加启动前置
mo issue workflow status <number>             # workflow 状态
mo issue workflow timeline <number>           # workflow 时间线
```

## Epic lifecycle commands

Epic lifecycle has two layers: **autopilot** (the day-to-day trio) and the
**terminal tail**. The autopilot trio is the default way to drive an epic —
see `mohist-create-epic` for the full semantics (idempotency, running-but-idle,
auto-advancement). Below is the command surface only.

| Operation | CLI | Effect |
|---|---|---|
| Start | `mo epic start <id-or-number>` | `idle` → `running`; auto-advances to the first startable linked issue. Idempotent against `running`. |
| Pause | `mo epic pause <id-or-number>` | `running` → `paused`; stops future advancement, does NOT interrupt the in-progress issue. Idempotent against `paused`. |
| Resume | `mo epic resume <id-or-number>` | `paused` → `running`; re-evaluates readiness and advances. Idempotent against `running`. |
| Done | `mo epic done <id-or-number>` | Terminal `done` (requires no open linked issues / all linked issues terminal; cancelled issues satisfy readiness but do not count as delivered). |
| Close | `mo epic close <id-or-number>` | Terminal `closed` (abandon the milestone). |
| Link | `mo epic link <epic> <issue>` | Link an issue to the epic as a member. |
| Unlink | `mo epic unlink <epic> <issue>` | Unlink a member issue. |
| Show | `mo epic show <id-or-number>` | Detail + `progress.nextIssue` / `progress.nextIssueReason` (used to inspect running-but-idle). |

Read-only and helpers:

```bash
mo epic list                       # 当前 project 的 epic 列表
mo epic update <id-or-number>      # 修改 title/description/priority
```

Project-wide listing (use sparingly — the project group also exposes workflow
template/config which is unrelated to epics):

```bash
mo project workflow profile list   # 列出 workflow profile
mo label list                      # 当前 project 的可用标签
```

## Common flags

All issue/epic commands accept these unless documented otherwise:

| Flag | Meaning |
|---|---|
| `--project <name>` / `--project-id <id>` | Target project; canonical is `--project`. `--project-id` is a backwards-compatible alias. |
| `-o, --output <table\|json>` | Output format (table by default; many commands default to JSON). |
| `--message <m>` / `-m <m>` | Required by `mo issue reject` to carry the change-request reason. |

For the full flag surface on any command, run `mo <cmd> --help`.
