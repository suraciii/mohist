---
name: mohist
description: 执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue 或 epic，查看项目状态或日志，或任何涉及 Mohist issue/epic/workflow 的操作时使用。旧 Node CLI 已移除。
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
- Treat local issue artifacts under `openspec/changes/<issue>/` as authoritative when the user provides an issue context.
- Keep changes scoped to the current issue; do not substitute adjacent cleanup or legacy behavior unless the issue explicitly requires it.
- For local verification, prefer the smallest relevant command or test filter instead of broad full-repo runs.

Boundaries:

- Do not rely on the removed pre-Orleans Node CLI or its workflow/runtime behavior.
- Do not assume Mohist server APIs must be running for purely local CLI or filesystem tasks unless the task explicitly requires server interaction.
- Do not mutate internal runtime data under `.mohist/skills` when the task is about shared coder-agent skills.

## Creating issues

This skill owns the **mechanics** of turning requirement content into a Mohist issue. The `mohist-explore` skill produces the PRD *content* (User Voice, Product Shape, Domain Model, acceptance criteria, non-goals); this skill wraps that content with the right frontmatter, recommends a workflow and risk, and runs the CLI to create the issue after user confirmation.

The split is deliberate: `mohist-explore` is about thinking clearly and is immune to CLI changes; this skill tracks the CLI version and owns every execution detail.

### Body content vs frontmatter

An issue body has two layers:

- **Frontmatter** (parsed by the CLI): carries `recommended_workflow`, `recommended_workflow_reason`, and `risk`. The CLI uses these to pre-fill `--workflow-profile` and `--risk`; explicit CLI flags still override frontmatter values.
- **Structured content** (human-readable): the five sections produced by `mohist-explore` — User Voice, Product Shape, Domain Model, Acceptance Criteria, Non-Goals.

When the user arrives with content from `mohist-explore`, your job is to add the frontmatter layer and hand the full file to `mo issue create <title> --body-file <file>`.

### Workflow discovery (required before recommending)

Before recommending a workflow, discover what is available:

```bash
mo workflow list --described
```

This prints each workflow profile's `id`, display name, description, and `suitable_for` tags, for example:

```
mohist/default — Mohist Default Workflow
  Standard autonomous plan→build→check→merge pipeline.
  Suitable for: feature, bugfix, refactor, default
```

Parse the output to collect each profile's `id` and its `suitable_for` tags. If the line reads `(not specified)`, treat the profile as having no declared suitability signal.

### Matching content to a workflow

Match the PRD content to a profile using rule-based comparison against `suitable_for` tags:

1. Summarize the content in 3–5 keywords (for example: `ui`, `feature`, `bug`, `docs`, `refactor`, `infra`, `security`).
2. For each discovered profile, score it by counting how many of its `suitable_for` tags overlap the keywords.
3. Recommend the profile with the highest overlap.
4. Write `recommended_workflow_reason` as one short sentence that names the matched tag(s) and ties them to the content — not a copy of the profile description. Example: `Content covers a UI affordance and a feature addition, matching feature-flow's suitable_for: ui, feature.`

If two profiles tie, prefer the more specific one (fewer, more targeted tags). If all candidates score zero, fall back to the default below.

### Default fallback when nothing matches

When no profile's `suitable_for` description matches the content (every candidate scores zero, `suitable_for` is unspecified for all profiles, or `mo workflow list --described` is unavailable), default to:

- `recommended_workflow: mohist/default`
- `recommended_workflow_reason: No specific workflow matched the issue content; falling back to mohist/default.`

Never leave `recommended_workflow` blank. The default profile is always a safe choice because it is guaranteed to exist.

### Risk assessment

Set the `risk` frontmatter field to one of `low`, `medium`, or `high` based on the content:

- `low`: isolated change, single subsystem, no migration or API impact, covered by existing tests.
- `medium`: touches multiple subsystems, requires a schema migration, or changes a public CLI/API contract.
- `high`: large blast radius (auth, workflow runtime, persistence), cross-cutting refactor, or irreversible action without a rollback path.

Document the risk driver in the `Product Shape` or `Acceptance Criteria` section (as a one-line note) so the reviewer can validate the rating.

### Frontmatter format

The body file MUST start with a YAML frontmatter block delimited by leading and trailing `---` lines. The frontmatter carries the workflow recommendation and risk; the structured sections (produced by `mohist-explore`) follow.

Supported fields:

| Field | Required | Description |
|---|---|---|
| `recommended_workflow` | yes | Profile id from `mo workflow list --described`, or `mohist/default` as fallback. |
| `recommended_workflow_reason` | yes | One sentence explaining why this workflow was chosen, referencing matched `suitable_for` tags or the fallback rationale. Multi-line values use the YAML `\|` block scalar. |
| `risk` | yes | One of `low`, `medium`, `high`. |

Unrecognized keys are ignored by the CLI; do not invent additional fields. The full frontmatter + body template is at `references/issue-templates.md`.

### User confirmation flow

Before creating the issue, present the recommendation to the user and wait for explicit confirmation:

1. Show a compact summary:
   - `recommended_workflow` and a one-line `recommended_workflow_reason`
   - `risk` and the driver behind it
   - The five section headings with a one-sentence gist of each
2. Ask the user to confirm, override the workflow, override the risk, or edit the body.
3. On confirm, run `mo issue create <title> --body-file <produced-file>` with no additional workflow/risk flags — the CLI applies the frontmatter values.
4. On override, update the frontmatter (or pass `--workflow-profile` / `--risk` explicitly) and then create. Always honor the user's final choice over the agent's recommendation.

Never run `mo issue create --body-file` without confirmation. The body file is advisory until the user approves it.

### Refactor label discipline

`refactor` is only for technical refactoring: changing internal code or architecture to reduce complexity, improve comprehensibility, and lower the cost of future change — **without changing observable behavior**.

Do **not** label something `refactor` when it changes:

- the product form or user flow,
- state semantics,
- the CLI / API / Web UI contract.

Those are `feature`, `improvement`, `design`, or `bug` — never `refactor`.

### Priority guidance

When assigning priority, use lowercase (`p0`–`p3`):

- `p0`: actively breaking a core workflow, the user cannot continue, or there is data/merge safety risk.
- `p1`: a core flow is visibly impaired but has a workaround; or it persistently misleads the user's judgment.
- `p2`: an important experience improvement, observability gap, or local flow friction.
- `p3`: low-risk polish, performance, or copy changes.

### End-to-end creation checklist

- [ ] `mo workflow list --described` was run and parsed.
- [ ] `recommended_workflow` is populated (best match or `mohist/default`).
- [ ] `recommended_workflow_reason` references the matched `suitable_for` tags or states the fallback.
- [ ] `risk` is `low`, `medium`, or `high`, with the driver noted in the body.
- [ ] The body's sections appear in order: User Voice, Product Shape, [Domain Model], Acceptance Criteria, Non-Goals. Domain Model is optional (omit for pure technical changes).
- [ ] The user has confirmed the recommendation and body summary before `mo issue create` runs.

## Creating epics

This skill also owns the **mechanics** of creating a Mohist epic. An epic is an
organizational milestone that groups 3+ issues toward a single product goal; it
does **not** participate in workflow execution and has no risk or workflow
fields. Epic content is a lightweight milestone description, not the five-section
PRD used for issues.

### When to create an epic (and when not to)

Create an epic when ALL hold:

- The goal needs **3+ issues** to complete.
- There is a **clear milestone outcome** you can name in one sentence.
- You want to **track progress** toward that outcome as a unit.

Do NOT create an epic for:

- A single independent change → just create an issue.
- A fuzzy goal you can't yet bound → leave issues in backlog until it clarifies.
- Refactors or technical cleanup with no product milestone → use issues + labels.

When in doubt, start with issues; promote to an epic once the milestone shape is
clear. Over-creating epics is the failure mode to avoid.

### Epic shape (no frontmatter)

Unlike issues, an epic has **no frontmatter, no workflow, no risk**. Do not invent
frontmatter fields for an epic — they are ignored. An epic has only:
`title`, `description` (long markdown), `priority`, and a derived `status`.

The `description` follows the milestone template in `references/epic-templates.md`:
Goal, Background, Non-goals, Scope (the issues it will contain).

### Priority guidance for epics

Epic priority rates the **milestone's** importance, not any single issue's. Use
`p0`–`p3` (lowercase), same scale semantics as issues but applied to the milestone.

### Creating an epic

```bash
mo epic create "<title>" --description "<markdown>" --priority p2
# -d / --description: the milestone markdown (see epic-templates.md)
# -p / --priority: p0|p1|p2|p3
# --project <id>: target project (else active project)
```

Note: `mo epic create` currently takes the description inline via `-d` only; there
is no `--description-file` yet. For long descriptions, write the markdown to a
file first, then pass its contents to `-d` via your shell, or use the API. (A
`--description-file` flag to match `mo issue create --body-file` is tracked as a
follow-up.)

### Linking issues to an epic

```bash
mo epic link <epic-id-or-number> <issue-id-or-number>
mo epic unlink <epic-id-or-number> <issue-id>
```

Constraint: **an issue belongs to at most one primary epic.** Linking an issue
already in another epic fails with `DUPLICATE_EPIC_MEMBERSHIP`. Both args accept
id or number.

### Setting issue prerequisites (execution order)

When an epic's issues have a start order (issue B requires issue A first), record
it as prerequisites so the epic can advance one issue at a time without false
starts:

```bash
# CLI does not yet have a prerequisite command — use the API:
curl -X POST http://localhost:3456/api/projects/<project>/issues/<B>/prerequisites \
  -H "Content-Type: application/json" \
  -d '{"prerequisiteNumber": <A-number>}'
```

A starts first; B becomes start-blocked ("waiting for #A") until A is delivered,
then B is free to start. Prefer fewer prerequisites — only real data/scaffold/invariant dependencies.

### Lifecycle: done vs close

- `mo epic done <id>` — marks the milestone shipped. Requires **all** linked
  issues delivered; else fails with `EPIC_NOT_READY_TO_MARK_DONE`.
- `mo epic close <id>` — abandons the milestone (not done, just dropped).

Use `done` for completed milestones, `close` for cancelled ones.

### User confirmation flow

Before creating, present to the user and wait for confirmation:

1. `title`, a one-line `description` gist, and `priority`.
2. The planned linked-issue list (numbers + titles) — or state "link later".
3. On confirm, run `mo epic create`; then `mo epic link` for each planned issue.

Never create an epic without confirmation.

### End-to-end creation checklist

- [ ] The goal genuinely needs 3+ issues (not over-creating).
- [ ] `description` follows Goal/Background/Non-goals/Scope.
- [ ] `priority` is `p0`–`p3`.
- [ ] No frontmatter/workflow/risk fields invented.
- [ ] User confirmed title, description summary, priority, and link plan.
- [ ] Lifecycle choice (done later vs close) is understood.
