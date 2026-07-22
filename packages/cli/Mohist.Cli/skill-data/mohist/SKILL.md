---
name: mohist
description: "Perform Mohist issue, epic, and workflow operations against the current .NET backend/API/Web. Use when the user asks to create, view, start, approve, or close issues or epics, check project status or logs, or do anything involving Mohist issues, epics, or workflows. This skill is the entry dispatcher: judge the scenario first, then load the matching scenario skill. The full issue/epic lifecycle command surface also lives here (no separate scenario skill covers it). The legacy Node CLI has been removed."
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

The issue/epic lifecycle commands below (start, rebase, close, and the epic
autopilot trio) plus the WorkflowRun control commands are **not** covered by a
dedicated scenario skill — they are reference-level operations fully answerable
by `mo <cmd> --help`. They live here so the dispatcher is the single source of
truth for day-to-day driving of issues and runs, and an agent does not have to
guess, omit, or invent a command.

### Sibling skills

| Skill | When |
|---|---|
| `mohist-explore` | Distill a fuzzy idea into a bounded PRD; decide issue/epic and scope. |
| `mohist-create-issue` | Execute issue creation mechanics. |
| `mohist-create-epic` | Execute epic creation mechanics + autopilot lifecycle semantics. |

## Issue lifecycle commands

These commands manage the work item itself. They take `<number>` (issue number
in the active project, or use `--project` / `--project-id` to target another
project). WorkflowRun state changes use `mo run` below.

| Operation | CLI | Effect |
|---|---|---|
| Start | `mo issue start <number>` | Drive the issue into the workflow (Draft → Plan). |
| Done | `mo issue done <number>` | Mark externally delivered work Done after its workflow is stopped or completed. |
| Rebase | `mo issue rebase <number> [--base-branch <b>]` | Rebase the issue branch onto its base. |
| Close | `mo issue close <number>` | Close a completed/abandoned issue. |
| Reopen | `mo issue reopen <number>` | Reopen a closed issue. |

## WorkflowRun control commands

Use a Run ID or `--issue <number>` as the target. Use `--project` when the
issue belongs to another project.

| Operation | CLI | Effect |
|---|---|---|
| Approve | `mo run approve <run-id>` / `mo run approve --issue <number>` | Approve at an approval gate (Plan → Build, Check → Integrate). |
| Reject | `mo run reject <run-id> --message <m>` | Reject at an approval gate with a change request. |
| Retry | `mo run retry <run-id>` | Retry the current failure point and restore the manual-retry budget. |
| Rerun | `mo run rerun <run-id>` | Rerun the whole workflow from the beginning. |
| Rerun from stage | `mo run rerun <run-id> --from-stage <stage>` | Invalidate the target stage and everything after it, then rerun. |
| Pause | `mo run pause <run-id>` | Pause the run while preserving the `resume` entry. |
| Resume | `mo run resume <run-id>` | Continue a paused run. |
| Stop | `mo run stop <run-id> --yes` | **Terminal** stop. Use only when you intend to abandon the run. |

Key distinctions:

- **`pause` vs `stop`**: `pause` preserves a resume entry; `stop` is terminal and cannot be resumed. Automated `stop` calls require `--yes`.
- **`done` vs `close`**: `done` records delivered work after a terminal workflow; `close` cancels work that will not be delivered.
- **`retry` vs `rerun --from-stage`**: `retry` retries the current failure point; `rerun` re-runs the whole workflow from the beginning; `rerun --from-stage` invalidates one named stage and everything after it.
- **`reject` vs `stop`**: `reject` bounces back at an approval gate with a change request (the issue stays alive for another pass); `stop` ends the run.

Read-only and aux helpers (also useful while driving):

```bash
mo issue show <number>            # details + current stage/health
mo issue events <number>          # event stream
mo issue logs <number>            # logs
mo issue diff <number>            # current branch vs base diff
mo issue commits <number>         # commits on the issue branch
mo issue sessions <number>        # list coder sessions (plan/build/check/integrate…)
mo issue session transcript <number> <name>   # one session's conversation
mo issue session followup <number> <name> --text <t>  # push a follow-up instruction into a running session
mo issue comment add <number> --body <text>   # add a comment
mo issue prereq add <number> <prereq>         # add a start prerequisite
mo issue workflow status <number>             # workflow status
mo issue workflow timeline <number>           # workflow timeline
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
mo epic list                       # list epics of the current project
mo epic update <id-or-number>      # edit title/description/priority
```

Project-wide listing (use sparingly — the project group also exposes workflow
template/config which is unrelated to epics):

```bash
mo project workflow profile list   # list workflow profiles
mo label list                      # labels available in the current project
```

## Common flags

All issue/epic commands accept these unless documented otherwise:

| Flag | Meaning |
|---|---|
| `--project <name>` / `--project-id <id>` | Target project; canonical is `--project`. `--project-id` is a backwards-compatible alias. |
| `-o, --output <table\|json>` | Output format (table by default; many commands default to JSON). |
| `--message <m>` / `-m <m>` | Required by `mo run reject` to carry the change-request reason. |

For the full flag surface on any command, run `mo <cmd> --help`.
