---
name: mohist-explore
description: 从产品和用户视角探索 mohist 项目，发现功能缺陷、体验问题、设计机会和价值增长点。当用户想要探索代码库、发现改进点、审查用户体验、思考功能设计、或无目标地巡检产品时使用。触发词包括 "explore"、"探索"、"巡检"、"找问题"、"体验审查"、"功能设计"、"产品思考"。
---

# mohist-explore

Use this skill to explore Mohist from the product and user perspective, identify UX problems, verify flows, and surface improvement opportunities without drifting into internal runtime-skill behavior.

When using this skill:

- Explore from the outside in: user-visible flows, operator workflows, docs, CLI affordances, Web UI behavior, and failure recovery.
- Prefer concrete evidence from the current repository, local runtime behavior, and issue artifacts over assumptions.
- Distinguish product problems from implementation details, and explain the user impact before proposing fixes.
- Keep exploration scoped to shared Mohist product behavior, not removed internal runtime-skill systems.

Good triggers include:

- Exploring the product for UX gaps or regressions.
- Reviewing whether docs, shipped guidance, and current CLI behavior are aligned.
- Looking for workflow friction, confusing approval flows, missing guardrails, or broken setup paths.

Boundaries:

- Do not turn an exploration request into unrelated code cleanup.
- Do not treat `.mohist/skills` runtime behavior as the target unless the issue explicitly concerns that area.
- Do not depend on stale pre-Orleans command surfaces when validating current product behavior.

## Producing issues from exploration findings

When an exploration concludes and the user wants to create an issue, produce a **structured, frontmatter-annotated body file** and hand it off to `mo issue create --body-file`. The CLI parses the frontmatter automatically and uses it to pre-fill `--workflow-profile` and `--risk`; explicit CLI flags still override frontmatter values.

The body file is the only contract between explore and runtime — make it self-contained, machine-readable, and reviewable.

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

Parse the output to collect each profile's `id` and its `suitable_for` tags. The `suitable_for` line lists the kinds of work the profile is designed to handle. If the line reads `(not specified)`, treat the profile as having no declared suitability signal.

### Matching exploration context to a workflow

Match the exploration findings to a profile using rule-based comparison against `suitable_for` tags:

1. Summarize the exploration context in 3–5 keywords (for example: `ui`, `feature`, `bug`, `docs`, `refactor`, `infra`, `security`).
2. For each discovered profile, score it by counting how many of its `suitable_for` tags overlap the exploration keywords.
3. Recommend the profile with the highest overlap.
4. Write `recommended_workflow_reason` as one short sentence that names the matched tag(s) and ties them to the findings — not a copy of the profile description. Example: `Findings touch UI affordances and a feature addition, matching feature-flow's suitable_for: ui, feature.`

If two profiles tie, prefer the more specific one (the one with fewer, more targeted tags). If all candidates score zero, fall back to the default below.

### Default fallback when nothing matches

When no profile's `suitable_for` description matches the exploration findings (every candidate scores zero, `suitable_for` is unspecified for all profiles, or `mo workflow list --described` is unavailable), default to:

- `recommended_workflow: mohist/default`
- `recommended_workflow_reason: No specific workflow matched the exploration findings; falling back to mohist/default.`

Never leave `recommended_workflow` blank. The default profile is always a safe choice because it is guaranteed to exist.

### Risk assessment

Set the `risk` frontmatter field to one of `low`, `medium`, or `high` based on the findings:

- `low`: isolated change, single subsystem, no migration or API impact, covered by existing tests.
- `medium`: touches multiple subsystems, requires a schema migration, or changes a public CLI/API contract.
- `high`: large blast radius (auth, workflow runtime, persistence), cross-cutting refactor, or irreversible action without a rollback path.

Document the risk driver in the `Background` or `Acceptance criteria` section so the reviewer can validate the rating.

### Frontmatter format

The body file MUST start with a YAML frontmatter block delimited by leading and trailing `---` lines. The frontmatter carries the workflow recommendation and risk; the structured sections carry the human-readable content.

Supported fields:

| Field | Required | Description |
|---|---|---|
| `recommended_workflow` | yes | Profile id from `mo workflow list --described`, or `mohist/default` as fallback. |
| `recommended_workflow_reason` | yes | One sentence explaining why this workflow was chosen, referencing matched `suitable_for` tags or the fallback rationale. Multi-line values use the YAML `|` block scalar. |
| `risk` | yes | One of `low`, `medium`, `high`. |

Unrecognized keys are ignored by the CLI; do not invent additional fields.

### Body section template

Below the frontmatter, write four sections in this exact order. Each section starts with a level-2 (`##`) heading.

- `## Background` — concrete context distilled from the exploration: what was observed, where, and why it matters to users. Cite evidence (commands run, flows traced, files inspected).
- `## Goal` — the single user-visible outcome this issue will deliver. One paragraph, no implementation detail.
- `## Non-goals` — explicit list of what this issue will NOT do, to prevent scope creep. Each item on its own line.
- `## Acceptance criteria` — a checklist (`- [ ]`) of observable, verifiable conditions. Each item must be testable by someone who did not write the change.

The full template — including the frontmatter block and all four sections — is packaged at `references/issue-body-template.md`. Render it with `mo skills get mohist-explore --full` when you need a copy-paste starting point.

### User confirmation flow

Before creating the issue, present the produced recommendation to the user and wait for explicit confirmation:

1. Show a compact summary:
   - `recommended_workflow` and a one-line `recommended_workflow_reason`
   - `risk` and the driver behind it
   - The four section headings with a one-sentence gist of each
2. Ask the user to confirm, override the workflow, override the risk, or edit the body.
3. On confirm, run `mo issue create <title> --body-file <produced-file>` with no additional workflow/risk flags — the CLI will apply the frontmatter values.
4. On override, update the frontmatter (or pass `--workflow-profile` / `--risk` explicitly) and then create. Always honor the user's final choice over the agent's recommendation.

Never run `mo issue create --body-file` without confirmation. The body file is advisory until the user approves it.

### End-to-end handoff checklist

- [ ] `mo workflow list --described` was run and parsed.
- [ ] `recommended_workflow` is populated (best match or `mohist/default`).
- [ ] `recommended_workflow_reason` references the matched `suitable_for` tags or states the fallback.
- [ ] `risk` is `low`, `medium`, or `high`, with the driver documented in the body.
- [ ] Body contains `## Background`, `## Goal`, `## Non-goals`, `## Acceptance criteria` in order.
- [ ] User has confirmed the recommendation and body summary before `mo issue create` runs.
