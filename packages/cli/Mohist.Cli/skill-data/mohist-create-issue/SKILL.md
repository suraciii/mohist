---
name: mohist-create-issue
description: Mechanical execution of creating a Mohist issue: pick the template, wrap the content with frontmatter, recommend workflow/risk, classify with the label catalog, and run mo issue create after confirmation. Use when the user wants to turn an explored requirement into an issue. Trigger phrases include "create an issue", "new issue", "mo issue create", "label an issue". The issue-vs-epic decision is made by mohist-explore.
---

# mohist-create-issue

This skill owns the **mechanics** of turning requirement content into a Mohist issue. The `mohist-explore` skill produces the requirement clarification (the thinking — what problem, what target shape, what boundary, what domain constraints, what acceptance, what non-goals); this skill picks the right issue template, fills it, wraps it with frontmatter, recommends a workflow and risk, classifies with labels, and runs the CLI after confirmation.

The split is deliberate: `mohist-explore` is about thinking clearly (including the issue-vs-epic decision) and is immune to CLI changes; this skill tracks the CLI version and owns every execution detail for issue creation.

## Pick the issue template (required before writing the body)

An issue body's shape comes from a built-in template, not from this skill. Discover and select it:

```bash
mo issue template list          # metadata only: id, name, description
mo issue template view <id>     # full body, with per-section guidance comments
```

Select by reading the **descriptions** and applying one boundary question — *does external behavior change?*

| Answer | Template |
|---|---|
| External behavior **changes** in a user-perceivable way (new feature, or iteration of an existing one) | `feature` |
| Behavior **deviates from correct** — an invariant is violated (functional or non-functional bug) | `bug` |
| External behavior **unchanged**; value is internal (refactor, test coverage, optimization) | `refactor` |

Once selected, `mo issue template view <id>` returns the raw body. It is markdown with two things per section: an HTML comment (`<!-- ... -->`) carrying the per-section writing instructions, and a `<placeholder>` line. **Read the comments — they tell you exactly what to write and what to forbid in that section.** Fill the body by replacing each `<placeholder>` with the matching piece of the requirement clarification from `mohist-explore`. The HTML comments may be left in place (they are hidden in rendered markdown; they do not need stripping).

## Universal writing rules (apply to every section, stated once here)

These cut across all three templates and all sections; do not restate them inside the body.

An issue body is the working context for the agent that will plan and build it — usually the only context it gets. Write it as such:

- **State what we want.** The need and the acceptance, unambiguous to a reader who never saw the conversation that produced it.
- **Record the decisions.** Anything the agent cannot derive from the code — target shape, trade-offs already made, how sibling issues divide the work — must be written into the body, or it will be re-decided inconsistently.
- **Provide decision-aiding context.** Evidence anchors, impact scope, and boundaries that change what the agent will do. Background that changes nothing is filler.
- **Cut what the agent can look up.** Pixel measurements, field inventories, file lists — anything cheaply obtainable from the code or a quick check is noise, not context.

Style rules:

- **Literal, not figurative.** No metaphors, no anthropomorphism ("the CLI lies", "dead code", "silently drops"). Describe what actually happens in plain terms.
- **Product source language.** Use the names the product already uses for its own concepts — issue, workflow, stage, label, prerequisite, comment, epic. Do not invent synonyms.
- **No source paths in the body.** The body must not cite source paths, file names, line numbers, or symbol names. Mapping to code is the Plan stage's job.
- **Planner-actionable.** Every section must be reproducible / observable / quantifiable. The body's primary consumer is the AI planner at Plan time — vague input yields a vague plan.

The per-section guidance comments in the template carry the section-specific rules (what to write, what to forbid, when to delete an optional section). This skill states the universals once; the template states the particulars.

## Body content vs frontmatter

An issue body has two layers:

- **Frontmatter** (parsed by the CLI): carries `recommended_workflow`, `recommended_workflow_reason`, and `risk`. The CLI uses these to pre-fill `--workflow-profile` and `--risk`; explicit CLI flags still override frontmatter values.
- **Structured content** (human-readable): the template body, with placeholders filled from the `mohist-explore` clarification.

Write the filled body to a temp file and hand it to `mo issue create <title> --body-file <file>`.

## Workflow discovery (required before recommending)

Before recommending a workflow, discover what is available:

```bash
mo workflow list
```

This prints each enabled workflow profile's `id`, display name, and natural-language description, for example:

```
mohist/local — Mohist Local Workflow
  Default general-purpose workflow. Runs the local plan→build→check→merge pipeline against the active project.

mohist/github-pr — Mohist GitHub PR Workflow
  Default general-purpose workflow. Drives the same plan→build→check→merge pipeline and publishes the result as a GitHub Pull Request via the `gh` CLI on the runner host and `gh auth login` against the target repository.
```

Use this output as the source of truth for the `id` of any profile you write into the frontmatter. The natural-language description tells you what the profile does; you do not parse or score structured tags.

## Selecting a workflow

Pick `recommended_workflow` using **default or operator choice** — there is no tag-based scoring. Concretely:

1. **Default profile for the project.** The project is configured with a default workflow profile (the operator's standing choice for issues on this project). Use that id if it appears in the `mo workflow list` output as enabled. This is the recommended path.
2. **Operator-chosen enabled id.** If the operator explicitly names a profile (in this turn, or in prior context for this issue), use that id — provided it is enabled in the `mo workflow list` output. Reject the operator's choice politely if the id is not enabled, and ask them to enable it or pick another.
3. **First enabled profile as last resort.** If there is no project default and the operator has not chosen one, the first enabled profile, else fail with an actionable error.

Do not score profiles against content keywords. Do not look for suitability tags. The natural-language description exists to tell a human reader what the profile does; it is not a scoring input for the agent.

If workflow discovery is unavailable, stop before writing frontmatter and ask the user to fix discovery first. If no profile is enabled for the project, stop before writing frontmatter and ask the user to enable a workflow first. Do not invent a recommendation or create frontmatter until discovery returns at least one enabled profile.

## Writing `recommended_workflow_reason`

`recommended_workflow_reason` is one short sentence in natural language that explains **why** the chosen id was selected. It does not cite matched tags, scoring, or any rule the operator cannot verify by reading the description. Choose the wording to match the path you took:

- Default profile used: e.g. `Using the project's default workflow profile.` or `Selected the project's configured default workflow.`
- Operator-chosen id: e.g. `Using mohist/github-pr per your instruction.` or `Selected mohist/github-pr as you requested for this issue.`
- First-enabled fallback: e.g. `No project default is configured; using the first enabled workflow returned by mo workflow list.`

Keep it to one sentence. The YAML `|` block scalar is the right tool if the sentence wraps across two lines. Do not pad the reason with restatements of the profile's own description — the description already lives in the system, not the frontmatter.

## Risk assessment

Set the `risk` frontmatter field to one of `low`, `medium`, or `high` based on the content:

- `low`: isolated change, single subsystem, no migration or API impact, covered by existing tests.
- `medium`: touches multiple subsystems, requires a schema migration, or changes a public CLI/API contract.
- `high`: large blast radius (auth, workflow runtime, persistence), cross-cutting refactor, or irreversible action without a rollback path.

Document the risk driver in the `Product Shape` or `Acceptance Criteria` section (as a one-line note) so the reviewer can validate the rating.

## Frontmatter format

The body file MUST start with a YAML frontmatter block delimited by leading and trailing `---` lines. The frontmatter carries the workflow recommendation and risk; the filled template body follows.

Supported fields:

| Field | Required | Description |
|---|---|---|
| `recommended_workflow` | yes | Profile id from `mo workflow list`, chosen by the default-or-operator rule above (or the first enabled profile as last resort). |
| `recommended_workflow_reason` | yes | One short natural-language sentence explaining the choice (default, operator-chosen, or first-enabled fallback). Multi-line values use the YAML `\|` block scalar. |
| `risk` | yes | One of `low`, `medium`, `high`. |

Unrecognized keys are ignored by the CLI; do not invent additional fields.

## Issue labeling

Before `mo issue create`, classify the issue with labels — be proactive, never submit an unclassified issue.

1. Run `mo label list` and read each label definition's `description`.
2. Match the issue content against those descriptions using your own semantic judgment — no keyword rules. When a description fits, apply it with `-l key=value` (repeatable for several labels).
3. **If the catalog is empty or nothing matches, invent a few sensible `key=value` labels yourself** (e.g. `module:auth`, `kind:bug`) and apply them. An unclassified issue is the failure mode.
4. Include the selected labels (including any you invented) in the confirmation summary; honor the user's overrides.

The catalog is descriptive — a manual the agent reads — not a governance constraint. Classification is the agent's job; the server only serves the catalog.

## Priority guidance

When assigning priority, use lowercase (`p0`–`p3`):

- `p0`: actively breaking a core workflow, the user cannot continue, or there is data/merge safety risk.
- `p1`: a core flow is visibly impaired but has a workaround; or it persistently misleads the user's judgment.
- `p2`: an important experience improvement, observability gap, or local flow friction.
- `p3`: low-risk polish, performance, or copy changes.

## User confirmation flow

Before creating the issue, present the recommendation to the user and wait for explicit confirmation:

1. Show a compact summary:
   - The chosen **template** (feature/bug/refactor) and why
   - `recommended_workflow` and a one-line `recommended_workflow_reason`
   - `risk` and the driver behind it
   - `priority` (if you are setting it)
   - The section headings with a one-sentence gist of each
   - The selected labels (`key=value`), including any you invented when the catalog was empty or had no match
2. Ask the user to confirm, override the template, override the workflow, override the risk, or edit the body.
3. On confirm, run `mo issue create <title> --body-file <produced-file>` with no additional workflow/risk flags (the CLI applies the frontmatter values); append `-l key=value` for each selected label.
4. On override, update the frontmatter (or pass `--workflow-profile` / `--risk` explicitly) and then create. Always honor the user's final choice over the agent's recommendation.

Never run `mo issue create --body-file` without confirmation. The body file is advisory until the user approves it.

## End-to-end creation checklist

- [ ] The issue passes the Scope gate defined in `mohist-explore` (one-sentence standalone value, every scope item serves that sentence, stop-here test) — **regardless of how the requirement content was produced**. If a check fails, fix the split before creating; do not create an issue that only has value together with a sibling.
- [ ] `mo issue template list` was run; a template selected via the boundary question (behavior changes / deviates / unchanged).
- [ ] `mo issue template view <id>` was run; the per-section guidance comments were read and followed.
- [ ] Each `<placeholder>` in the body is replaced by content from the `mohist-explore` clarification; no placeholder remains.
- [ ] The body obeys the universal writing rules: literal, product source language, no source paths, planner-actionable.
- [ ] `mo workflow list` was run and parsed.
- [ ] `recommended_workflow` is populated (project default, operator-chosen enabled id, or first enabled profile).
- [ ] `recommended_workflow_reason` is one natural-language sentence explaining the choice (default, operator, or first-enabled fallback) — no tag citations.
- [ ] `risk` is `low`, `medium`, or `high`, with the driver noted in the body.
- [ ] `mo label list` was run; labels applied via `-l key=value` (invented when the catalog is empty or has no match); confirmed with the user.
- [ ] The user has confirmed the template, workflow, risk, and body summary before `mo issue create` runs.
