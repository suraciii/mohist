---
name: mohist-explore
description: 把模糊的产品想法提炼成清晰的、有边界的 Mohist issue 需求文档。当用户带着一句话、一个模糊念头或未沉淀的改进意图，需要探索当前产品形态和技术实现，最终产出一份用户视角、产品视角、领域视角三段协作的 PRD 时使用。触发词包括 "提炼需求"、"写 PRD"、"沉淀 issue"、"需求文档"、"探索"、"完善 issue"。
---

# mohist-explore

Use this skill to **distill** a fuzzy idea into a clear, bounded product requirement document (PRD) for a Mohist issue.

The input is a seed — a sentence, a vague hunch, an improvement intent, or a half-formed thought. The output is a structured PRD that is clear enough for the Plan stage to act on. This skill is about *thinking clearly*, not about operating the CLI. Once the PRD content is finalized, hand it off to the `mohist` skill to actually create the issue.

## When to use

- The user arrives with a fuzzy idea and wants it turned into a Mohist issue.
- The user has a few sentences and needs them sharpened into something with a real boundary.
- An issue exists but its body is too vague, too wide, or missing the domain context needed to plan implementation.
- The user wants to think through a product change before committing it to an issue.

Do **not** use this skill for unfocused product patrols ("go find anything broken"). That is a different mode. This skill always starts from a seed the user provides.

## The three-voice model

A good PRD is a collaboration between three perspectives. They are **not** peers — they form a dependency chain: Product builds on User, Domain builds on Product. The skill works through them in order, and each voice has its own exploration direction and a gate it must pass before the next voice begins.

| Voice | Who speaks | Explores | Language | Core question |
|---|---|---|---|---|
| **User Voice** | The user (you); the agent records faithfully | The user's real scenario | The user's own words | What do you actually need? Where do you get stuck? |
| **Product Shape** | The agent as PM | Current **product form** — what the user experiences today (Web UI, CLI, user journeys) | Product language | How does the need become a concrete product decision with a real boundary? |
| **Domain Model** | The agent as domain expert | Current **technical implementation** — how it works today, what constrains it | Domain language | How does this product decision hold up in the domain? What are the invariants and constraints? |

The most common PRD failure is skipping User Voice and jumping straight to implementation. This skill enforces that User Voice comes first, even when the idea feels obvious.

## Workflow

### 1. Capture User Voice

Act as an interviewer. The goal here is to **record**, not to solve.

- Restate the user's seed in the user's own words. Do not translate into product or technical terms yet.
- Ask about the scenario: when does this matter? What is the user trying to decide or do? Where does the current experience fail them?
- Capture intent, not solution. If the user proposes a fix ("just add a toggle"), ask what problem the toggle solves and record the problem.
- Do not invent requirements the user did not express.

**Gate:** Can the user read back the captured User Voice and say "yes, that is exactly what I mean"? If not, keep asking. You may not proceed until the user recognizes their own need in this section.

A minimal User Voice is one sentence naming the scenario and the need. Even for a clear-cut idea ("add dark mode"), require at least that — never skip the voice entirely.

### 2. Settle Product Shape

Switch to PM perspective. Explore the current product form, then translate User Voice into a concrete product decision.

- Explore what the user experiences today: the relevant Web UI pages, CLI commands, user journeys, and failure paths. Cite what you actually observe (pages visited, commands run, flows traced).
- Translate the need into a target product form: what will the user see or be able to do? What changes in their journey?
- Decide the boundary. State what is in scope and — just as importantly — what is not. A PRD that cannot name its non-goals is under-cooked.
- Make the trade-offs explicit. If two directions exist, name the one you chose and why.

**Gate:** Is the boundary clear? Are the non-goals brave enough (actually cutting things, not just listing safe trivia)? Does the Product Shape demonstrably resolve the User Voice — can you trace each user need to a product decision that addresses it? If not, refine.

### 3. Distill Domain Model

Switch to domain expert perspective. Explore the current technical implementation, then express the Product Shape in domain terms — just enough for the Plan stage to understand the problem, not a full technical design.

- Explore how the relevant area works today: the code paths, data models, and architectural constraints. Cite files and symbols you inspected.
- Name the key domain concepts, the invariants that must hold, and the constraints that shape the solution.
- Do **not** prescribe files, functions, database tables, or step-by-step implementation tasks. That belongs to the Plan stage. Domain Model is about the *problem space*, not the solution space.
- If the Product Shape turns out to be infeasible or more complex than expected, stop and say so — then go back and revise Product Shape (see Iteration below).

**Gate:** Are the domain concepts accurate (verifiable against the code)? Are the real constraints identified? Is this the minimum domain context needed to plan — or have you drifted into premature design? Trim anything that looks like an implementation recipe.

### 4. Converge into a PRD

Assemble the three voices into a single PRD using the structure in `references/issue-body-template.md`. The PRD is pure content — it does not carry frontmatter, workflow recommendations, or risk ratings. Those are the `mohist` skill's job at issue-creation time.

Present the assembled PRD to the user for confirmation before any issue is created.

## Iteration

The voices are serial, but not one-way. When a later voice reveals a gap in an earlier one, you may go back — but you must **say so explicitly**:

- "Writing the Domain Model surfaced a constraint that Product Shape missed. Going back to revise Product Shape: …"

Never silently rewrite an earlier section. The user must always know which voice is currently active and why a previous decision is being revisited.

Common backtrack triggers:
- Domain Model finds Product Shape is infeasible → revise Product Shape (and check User Voice still holds).
- Product Shape realizes User Voice was actually two needs → split or revise User Voice.
- User feedback on the converged PRD challenges a specific voice → revise only that voice, then re-check dependents.

## Boundaries

- Do not include issue-creation execution details (frontmatter, `mo issue create`, workflow ids, risk fields). That is the `mohist` skill's responsibility. This skill produces PRD *content*; `mohist` turns it into an issue.
- Do not prescribe implementation (files, functions, tables, task breakdown). That is the Plan stage's responsibility.
- Do not start from a blank slate and patrol for random problems. Always begin from a user-provided seed.
- Do not let Domain Model grow into a technical design document. Keep it to the minimum domain context needed to understand the requirement.

## Handoff

When the PRD is confirmed:

1. Point the user to the `mohist` skill to create the issue: the `mohist` skill will read the PRD, recommend a workflow and risk, generate frontmatter, and run `mo issue create` after user confirmation.
2. The PRD content (the three voices + acceptance criteria + non-goals) becomes the issue body. `mohist` is responsible for the frontmatter and CLI mechanics.

The full PRD template is at `references/issue-body-template.md`.
