---
name: mohist-create-issue
description: 创建 Mohist issue 的机械执行：给 PRD 内容加 frontmatter、推荐 workflow/risk、用 label 目录分类打标、确认后跑 mo issue create。当用户要把已探索好的需求落成 issue 时使用。触发词包括 "创建 issue"、"建 issue"、"new issue"、"mo issue create"、"给 issue 打标"。issue 还是 epic 的决策由 mohist-explore 完成。
---

# mohist-create-issue

This skill owns the **mechanics** of turning requirement content into a Mohist issue. The `mohist-explore` skill produces the PRD *content* (User Voice, Product Shape, Domain Model, acceptance criteria, non-goals) and decides whether the work is an issue or an epic; this skill wraps that content with the right frontmatter, recommends a workflow and risk, classifies with labels, and runs the CLI to create the issue after user confirmation.

The split is deliberate: `mohist-explore` is about thinking clearly (including the issue-vs-epic decision) and is immune to CLI changes; this skill tracks the CLI version and owns every execution detail for issue creation.

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
mohist/local — Mohist Local Workflow
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

When no profile's `suitable_for` description matches the content (every candidate scores zero, or `suitable_for` is unspecified for all profiles), default to:

- `recommended_workflow`: the first enabled profile, else fail with an actionable error.
- `recommended_workflow_reason: No specific workflow matched the issue content; using the first enabled profile.`

If workflow discovery is unavailable, stop before writing frontmatter and ask the user to fix discovery first. If no profile is enabled for the project, stop before writing frontmatter and ask the user to enable a workflow first. Do not invent a recommendation or create frontmatter until discovery returns at least one enabled profile.

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
| `recommended_workflow` | yes | Profile id from `mo workflow list --described`, or the first enabled profile as fallback. |
| `recommended_workflow_reason` | yes | One sentence explaining why this workflow was chosen, referencing matched `suitable_for` tags or the fallback rationale. Multi-line values use the YAML `\|` block scalar. |
| `risk` | yes | One of `low`, `medium`, `high`. |

Unrecognized keys are ignored by the CLI; do not invent additional fields. The full frontmatter + body template is at `references/issue-templates.md`.

### Issue labeling

Before `mo issue create`, classify the issue with labels — be proactive, never submit an unclassified issue.

1. Run `mo label list` and read each label definition's `description`.
2. Match the issue content against those descriptions using your own semantic judgment — no keyword rules. When a description fits, apply it with `-l key=value` (repeatable for several labels).
3. **If the catalog is empty or nothing matches, invent a few sensible `key=value` labels yourself** (e.g. `module:auth`, `kind:bug`) and apply them. An unclassified issue is the failure mode.
4. Include the selected labels (including any you invented) in the confirmation summary; honor the user's overrides.

The catalog is descriptive — a manual the agent reads — not a governance constraint. Classification is the agent's job; the server only serves the catalog.

### Priority guidance

When assigning priority, use lowercase (`p0`–`p3`):

- `p0`: actively breaking a core workflow, the user cannot continue, or there is data/merge safety risk.
- `p1`: a core flow is visibly impaired but has a workaround; or it persistently misleads the user's judgment.
- `p2`: an important experience improvement, observability gap, or local flow friction.
- `p3`: low-risk polish, performance, or copy changes.

### User confirmation flow

Before creating the issue, present the recommendation to the user and wait for explicit confirmation:

1. Show a compact summary:
   - `recommended_workflow` and a one-line `recommended_workflow_reason`
   - `risk` and the driver behind it
   - `priority` (if you are setting it)
   - The five section headings with a one-sentence gist of each
   - The selected labels (`key=value`), including any you invented when the catalog was empty or had no match
2. Ask the user to confirm, override the workflow, override the risk, or edit the body.
3. On confirm, run `mo issue create <title> --body-file <produced-file>` with no additional workflow/risk flags (the CLI applies the frontmatter values); append `-l key=value` for each selected label.
4. On override, update the frontmatter (or pass `--workflow-profile` / `--risk` explicitly) and then create. Always honor the user's final choice over the agent's recommendation.

Never run `mo issue create --body-file` without confirmation. The body file is advisory until the user approves it.

### End-to-end creation checklist

- [ ] `mo workflow list --described` was run and parsed.
- [ ] `recommended_workflow` is populated (best match or first enabled profile).
- [ ] `recommended_workflow_reason` references the matched `suitable_for` tags or states the fallback.
- [ ] `risk` is `low`, `medium`, or `high`, with the driver noted in the body.
- [ ] The body's sections appear in order: User Voice, Product Shape, [Domain Model], Acceptance Criteria, Non-Goals. Domain Model is optional (omit for pure technical changes).
- [ ] `mo label list` was run; labels applied via `-l key=value` (invented when the catalog is empty or has no match); confirmed with the user.
- [ ] The user has confirmed the recommendation and body summary before `mo issue create` runs.
