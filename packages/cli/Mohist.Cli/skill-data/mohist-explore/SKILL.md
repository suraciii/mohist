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

## Writing principles

The PRD is a product document, not a technical design. Every section of the output must be:

- **Concise and precise.** No filler, no restating context, no throat-clearing. Each sentence carries information the next sentence depends on.
- **Literal, not figurative.** No metaphors and no anthropomorphism ("the CLI lies", "dead code", "silently drops", "wiring"). Describe what actually happens in plain terms: *the `--model` flag is accepted by the command but not persisted on the issue.*
- **Product source language.** Use the names the product already uses for its own concepts — issue, workflow, stage, label, prerequisite, comment, feedback, epic, repository. Do not invent fancy synonyms or borrowed jargon.
- **Product perspective for functional requirements.** Describe what the user sees and can do, not which files or symbols change. The PRD body must not cite source paths, file names, line numbers, or symbol names. Mapping to code is the Plan stage's job.
- **Domain concepts in domain language.** When the Domain Model section is present, name its concepts, invariants, and constraints in the vocabulary of the domain — not as a tour of the codebase.

The distinction that governs everything below: **you explore the code internally to ground your understanding, but the PRD you write down is product-facing prose. Code paths are your evidence, never your output.**

## The three-voice model

A good PRD is a collaboration between three perspectives. They are **not** peers — they form a dependency chain: Product builds on User, Domain builds on Product. The skill works through them in order, and each voice has its own exploration direction and a gate it must pass before the next voice begins.

| Voice | Who speaks | Explores | Language | Core question |
|---|---|---|---|---|
| **User Voice** | The user (you); the agent records faithfully | The user's real scenario | The user's own words | What do you actually need? Where do you get stuck? |
| **Product Shape** | The agent as PM | The **product form** the user experiences today (Web UI, CLI, user journeys) and the target form | Product language | How does the need become a concrete product decision with a real boundary? |
| **Domain Model** | The agent as domain expert | The **domain** the decision lives in — its concepts, invariants, and constraints. **Optional** for simple technical changes (see step 3). | Domain language | How does this product decision hold up in the domain? What are the invariants and constraints? |

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

- Explore what the user experiences today: the relevant Web UI pages, CLI commands, user journeys, and failure paths. This exploration is internal — use it to ground your understanding. Do not put source paths, file names, or symbol names in the PRD body; describe the product form in product language.
- Translate the need into a target product form: what will the user see or be able to do? What changes in their journey?
- Decide the boundary. State what is in scope and — just as importantly — what is not. A PRD that cannot name its non-goals is under-cooked.
- Make the trade-offs explicit. If two directions exist, name the one you chose and why.

**Gate:** Is the boundary clear? Are the non-goals brave enough (actually cutting things, not just listing safe trivia)? Does the Product Shape demonstrably resolve the User Voice — can you trace each user need to a product decision that addresses it? If not, refine.

### 3. Distill Domain Model (optional for simple technical changes)

Switch to domain expert perspective. Explore the current technical implementation, then express the Product Shape in domain terms — just enough for the Plan stage to understand the problem, not a full technical design.

**When to include this section.** Write the Domain Model when the requirement touches a non-trivial business domain — where invariants, lifecycle rules, or cross-aggregate constraints are part of what makes the decision hard. **Omit the section entirely** when the change is a pure technical correction (a flag that doesn't persist, a missing subcommand, a CLI/API parity gap) with no complex business scenario behind it. An omitted Domain Model is better than a padded one that restates Product Shape in code terms.

- Explore how the relevant area works today: the code paths, data models, and architectural constraints. This exploration is internal; do not cite files, symbols, or line numbers in the PRD body.
- Name the key domain concepts, the invariants that must hold, and the constraints that shape the solution — in the domain's own vocabulary, not as a codebase tour.
- Do **not** prescribe files, functions, database tables, or step-by-step implementation tasks. That belongs to the Plan stage. Domain Model is about the *problem space*, not the solution space.
- If the Product Shape turns out to be infeasible or more complex than expected, stop and say so — then go back and revise Product Shape (see Iteration below).

**Gate:** If the section is present, are the domain concepts accurate and stated in domain language? Are the real constraints identified? Is this the minimum domain context needed to plan — or have you drifted into premature design? Trim anything that looks like an implementation recipe. If the section is omitted, confirm the change genuinely has no complex business scenario worth capturing.

### 4. Scope: one issue or many?

By now you understand the need (User Voice), the target product form (Product Shape), and the domain it touches (Domain Model). Before converging, decide the output shape: **one issue, or several.**

Apply these rules **in this priority order** — a higher rule always overrides a lower one:

1. **Bounded context (hard rule).** If the change touches more than one bounded context (e.g. Issue + Agent/Session + Web App-Shell), it MUST be split — one issue per context's internal change. Different contexts have different models, invariants, and owners; bundling them hides complexity and couples review and rollback. This rule never yields to the ones below.
2. **Concern.** Within a context, if one issue would solve more than one concern, split it — one issue per concern. Tell-tale signs: you cannot name the issue in one phrase, or its acceptance criteria cluster into unrelated groups.
3. **Scale.** If a single-context, single-concern change is still too large to plan/build/check in one workflow run, split it along natural seams.

Then decide whether the split issues form an **epic**:

- If they share **one milestone goal** (a single product outcome that ties them together) → produce an **epic + one PRD per issue**. The epic description captures the milestone (Goal / Background / Non-goals / Scope); each child issue gets its own three-voice PRD scoped to its context + concern.
- If they are independent (no shared milestone) → produce **several standalone issue PRDs**, no epic.

If no rule triggers a split → single issue; proceed to Converge with one PRD.

#### Dependency order (whenever there are 2+ issues)

Splitting is not enough — the work advances one issue at a time, so work out the order. For each pair of issues ask: **can B start without A done?** A blocks B when:

- A defines a **data contract or model** B consumes (B needs A's shape to exist first).
- A provides **scaffolding** B mounts into (e.g. a route/page before the zones that fill it).
- A changes a **shared invariant** B relies on (A must land first so B builds on truth).

Produce as part of the output:

- A **dependency list**: "issue X requires issue Y" for each real dependency.
- A **suggested start order**: which issue(s) can start now, which wait, and which can run in parallel (no dependency between them).

Prefer fewer dependencies — if two issues seem tightly coupled, re-check whether they are really one issue, or whether the split seam is wrong. But do not invent fake independence: if B genuinely needs A's contract, say so. The `mohist` skill turns this list into issue prerequisites at creation time, so the epic can advance issue-by-issue without false starts.

**Gate:** Can you name, for each proposed issue, exactly one bounded context and one concern? If any issue still bundles two contexts or two concerns, split again. If you propose an epic, can you state its milestone goal in one sentence? For 2+ issues, is the dependency order stated — including which can run in parallel?

### 5. Converge

Branch on the scope decision:

- **Single issue:** assemble the three voices into one PRD using `references/issue-body-template.md`.
- **Epic + issues:** write the epic description (Goal / Background / Non-goals / Scope — shape in the `mohist` skill's `references/epic-templates.md`), then one PRD per child issue using `references/issue-body-template.md`, each scoped to its own context + concern. Include the dependency list and suggested start order from the Scope stage so the epic can be advanced one issue at a time.

The PRD content is pure content — it does not carry frontmatter, workflow recommendations, or risk ratings. Those are the `mohist` skill's job at creation time.

Present the assembled output (single PRD, or epic description + issue PRDs) to the user for confirmation before anything is created.

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
- The PRD body carries no source paths, file names, line numbers, or symbol names. You explore code to ground your understanding; the PRD itself is product-facing prose.
- When splitting, apply the rules strictly in priority order: bounded context → concern → scale. Do **not** split by UI surface (e.g. "the attention zone" vs "the pulse zone") or by phasing (e.g. "v1 snapshot" vs "v2 trend") — those are neither context nor concern boundaries, and they produce coupled, mis-scoped issues. Do not split by scale alone either: if context and concern are identical, keep it in one issue even if large (express the phasing inside that issue instead).
- Do not start from a blank slate and patrol for random problems. Always begin from a user-provided seed.
- Do not let Domain Model grow into a technical design document. Keep it to the minimum domain context needed to understand the requirement, or omit it.

## Handoff

When the output is confirmed:

- **Single issue:** point the user to the `mohist` skill — it adds frontmatter, recommends workflow/risk, and runs `mo issue create` after confirmation.
- **Epic + issues:** point the user to the `mohist` skill — it creates the epic (`mo epic create`), creates each issue (`mo issue create`), links them (`mo epic link`), and sets prerequisites. The epic description and each issue PRD become the bodies; `mohist` owns all CLI mechanics.

In both cases this skill produces only **content**; `mohist` owns the frontmatter and CLI execution.

The issue PRD template is at `references/issue-body-template.md`. The epic description shape (Goal / Background / Non-goals / Scope) is in the `mohist` skill's `references/epic-templates.md`.
