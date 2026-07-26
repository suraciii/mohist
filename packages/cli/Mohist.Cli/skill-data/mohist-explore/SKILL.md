---
name: mohist-explore
description: Distill requirement input at any maturity — a sentence, a vague hunch, a discussion conclusion, an existing requirement document, or a proposed issue split — into a clear, bounded requirement clarification for Mohist issues. The skill maps the input onto its lens questions, keeps what is already answered, and elicits only the gaps. Use before creating issues or epics. Trigger phrases include "distill the requirement", "write a PRD", "capture as an issue", "requirement doc", "explore", "flesh out an issue", "split into issues".
---

# mohist-explore

Use this skill to **distill** requirement input into a clear, bounded requirement clarification for a Mohist issue.

The input is requirement material at any maturity — a sentence, a vague hunch, a half-formed thought, a conclusion from a prior discussion, an existing requirement document, or an already-proposed issue split. The output is a requirement clarification that is clear enough for the `mohist-create-issue` skill to fill an issue template against. This skill is about *thinking clearly*; it does not define issue-body sections, prescribe their format, or touch the CLI. Section structure and per-section writing rules live in the issue templates; this skill only makes sure the right questions are answered before an issue is created.

## When to use

- The user arrives with a fuzzy idea and wants it turned into a Mohist issue.
- The user has a few sentences and needs them sharpened into something with a real boundary.
- The user has a settled requirement — a document, a discussed conclusion — and wants it sliced into issues (and possibly an epic) that each stand alone.
- An issue exists but its thinking is too vague, too wide, or missing the domain context needed to plan implementation.
- The user wants to think through a product change before committing it to an issue.

Do **not** use this skill for unfocused product patrols ("go find anything broken"). That is a different mode. This skill always starts from requirement input the user provides.

## The three thinking lenses

A clear requirement is reached by working through three perspectives in order. They are **not** peers — they form a dependency chain: Product builds on User, Domain builds on Product. Each lens is a way of *interrogating* the requirement; it does not prescribe a section of the eventual issue body. The lenses ensure the right questions are answered; the issue template (chosen later by `mohist-create-issue`) decides how those answers are written down.

| Lens | Who thinks | Explores | Core question |
|---|---|---|---|
| **User Voice** | The user (you); the agent records faithfully | The user's real scenario | What do you actually need? Where do you get stuck? |
| **Product Shape** | The agent as PM | The **product form** the user experiences today (Web UI, CLI, user journeys) and the target form | How does the need become a concrete product decision with a real boundary? |
| **Domain Model** | The agent as domain expert | The **domain** the decision lives in — its concepts, invariants, and constraints. **Optional** for simple technical changes. | How does this decision hold up in the domain? What invariants and constraints shape it? |

The most common failure is skipping User Voice and jumping straight to implementation. This skill enforces that User Voice comes first, even when the idea feels obvious.

## Thinking principles

These govern how you explore and how you record the user's thinking. They are not section-writing rules (those live in the templates); they are the discipline of clear thinking.

- **Concise and precise.** No filler, no restating context, no throat-clearing. Each sentence carries information the next sentence depends on.
- **Literal, not figurative.** No metaphors and no anthropomorphism ("the CLI lies", "dead code", "silently drops", "wiring"). Describe what actually happens in plain terms.
- **Product source language.** Use the names the product already uses for its own concepts — issue, workflow, stage, label, prerequisite, comment, feedback, epic, repository. Do not invent fancy synonyms or borrowed jargon.
- **Explore code internally, record in product/domain terms.** You explore the codebase to ground your understanding, but what you record for the user is product- and domain-facing prose. Code paths are your evidence, never your output.
- **Write for the implementing agent.** The clarification becomes the issue body, which is usually the only context the Plan/Build agent gets. Keep what aids its decisions — the need, the decisions already made, the boundary — and cut what it can cheaply look up in the code itself. The full body-writing rules live in `mohist-create-issue`'s universal writing rules.

## Workflow

### 0. Map the input onto the lenses

Before any lens work, lay the input against the lens questions and the Scope questions: which are already answered, which are open.

- For each answered question, record the answer **with its source** — the user's own words, the provided document, the prior discussion.
- Answered questions are settled. Do not re-interview or re-derive them; reopen one only when a later lens uncovers a contradiction, and say so explicitly (see Iteration).
- Work the lenses in order as usual, but within each lens elicit **only the unanswered questions**.

Both extremes are the same procedure, not separate modes: a one-sentence hunch answers nothing (every lens runs as a full interview); a settled requirement document may answer every lens question, in which case go straight to Scope. The gates still run either way — maturity changes how much you ask, never which bars must be cleared.

### 1. Capture User Voice

Act as an interviewer **for the unanswered questions**. The goal here is to **record**, not to solve. When the input already answers this lens, restate the captured voice with its source and move on.

- Restate the user's seed in the user's own words. Do not translate into product or technical terms yet.
- Ask about the scenario: when does this matter? What is the user trying to decide or do? Where does the current experience fail them?
- Capture intent, not solution. If the user proposes a fix ("just add a toggle"), ask what problem the toggle solves and record the problem.
- Do not invent requirements the user did not express.

**Gate:** Can the user read back the captured User Voice and say "yes, that is exactly what I mean"? If not, keep asking. You may not proceed until the user recognizes their own need.

A minimal User Voice is one sentence naming the scenario and the need. Even for a clear-cut idea ("add dark mode"), require at least that — never skip the voice entirely.

### 2. Settle Product Shape

Switch to PM perspective. Explore the current product form, then translate User Voice into a concrete product decision.

- Explore what the user experiences today: the relevant Web UI pages, CLI commands, user journeys, and failure paths. This exploration is internal — use it to ground your understanding. Describe the product form in product language, not source paths.
- Translate the need into a target product form: what will the user see or be able to do? What changes in their journey?
- Decide the boundary. State what is in scope and — just as importantly — what is not. A requirement that cannot name its non-goals is under-cooked.
- Make the trade-offs explicit. If two directions exist, name the one you chose and why.

**Gate:** Is the boundary clear? Are the non-goals brave enough (actually cutting things, not just listing safe trivia)? Does the Product Shape demonstrably resolve the User Voice — can you trace each user need to a product decision that addresses it? If not, refine.

### 3. Distill Domain Model (optional for simple technical changes)

Switch to domain expert perspective. Explore the current technical implementation, then express the Product Shape in domain terms — just enough for the Plan stage to understand the problem, not a full technical design.

**When to think this through.** Pursue this lens when the requirement touches a non-trivial business domain — where invariants, lifecycle rules, or cross-aggregate constraints are part of what makes the decision hard. **Skip it** when the change is a pure technical correction (a flag that doesn't persist, a missing subcommand, a CLI/API parity gap) with no complex business scenario behind it.

- Explore how the relevant area works today: the code paths, data models, and architectural constraints. This exploration is internal; do not cite files, symbols, or line numbers in what you record.
- Name the key domain concepts, the invariants that must hold, and the constraints that shape the solution — in the domain's own vocabulary.
- Do **not** prescribe files, functions, database tables, or step-by-step implementation tasks. That belongs to the Plan stage. This lens is about the *problem space*, not the solution space.
- If the Product Shape turns out to be infeasible or more complex than expected, stop and say so — then go back and revise Product Shape (see Iteration below).

**Gate:** If this lens was pursued, are the domain concepts accurate and stated in domain language? Are the real constraints identified? Is this the minimum domain context needed to plan — or have you drifted into premature design? Trim anything that looks like an implementation recipe. If skipped, confirm the change genuinely has no complex business scenario worth capturing.

### 4. Scope: one issue or many?

By now you understand the need (User Voice), the target product form (Product Shape), and the domain it touches (Domain Model). Before converging, decide the output shape: **one issue, or several.**

**Every issue is an MVP** — it must independently deliver product value. A finished issue must be valuable to the user on its own; a split that leaves a piece with no standalone value (e.g. a "frontend" issue with no backend, or a "backend" issue with no UI) is wrong.

When to split:

- **Different problem, or different bounded context → must split**, one issue per context. Problem, domain, and bounded context are the same axis — a bounded context is the DDD boundary around one problem domain.
- **Same bounded context → may split**, as long as each piece still delivers standalone value (e.g. along a concern or scale seam).

Exception: many small, scattered, low-cost changes across different problems and contexts aren't worth one issue each. Merge them into a single tracking issue for the batch.

Then decide whether the split issues form an **epic**:

- If they share **one milestone goal** (a single product outcome that ties them together) → produce an **epic + one requirement clarification per issue**. The epic description captures the milestone (Goal / Background / Non-goals / Scope); each child issue gets its own clarification scoped to its context + concern.
- If they are independent (no shared milestone) → produce **several standalone requirement clarifications**, no epic.

If no rule triggers a split → single issue; proceed to Converge with one clarification.

#### Dependency order (whenever there are 2+ issues)

Splitting is not enough — the work advances one issue at a time, so work out the order. For each pair of issues ask: **can B start without A done?** A blocks B when:

- A defines a **data contract or model** B consumes (B needs A's shape to exist first).
- A provides **scaffolding** B mounts into (e.g. a route/page before the zones that fill it).
- A changes a **shared invariant** B relies on (A must land first so B builds on truth).

Produce as part of the output:

- A **dependency list**: "issue X requires issue Y" for each real dependency.
- A **suggested start order**: which issue(s) can start now, which wait, and which can run in parallel (no dependency between them).

Prefer fewer dependencies — if two issues seem tightly coupled, re-check whether they are really one issue, or whether the split seam is wrong. But do not invent fake independence: if B genuinely needs A's contract, say so. The `mohist-create-epic` skill turns this list into issue prerequisites at creation time, so the epic can advance issue-by-issue without false starts.

**Gate:** Run three checks on **every** proposed issue; each must pass:

1. **One-sentence value.** State the issue's standalone value in one sentence: "after this issue alone, the user can/gets …". If the sentence only works by mentioning a sibling issue, the split is wrong.
2. **Every scope item serves that sentence.** Each piece of the issue's scope must contribute to its own value sentence. An item that only serves a *different* issue's value belongs in that issue.
3. **Stop-here test.** If the epic (or the whole plan) stopped right after this issue, what has shipped is still worth having.

Then check the split shape: different-context work is split one issue per context, within-context splits each still pass the three checks, and scattered trivial changes are bundled — not over-split. If you propose an epic, can you state its milestone goal in one sentence? For 2+ issues, is the dependency order stated — including which can run in parallel?

### 5. Converge

The output of this skill is a **requirement clarification** — the answers gathered through the three lenses (the user's need, the product decision and its boundary, the domain constraints if any) plus the scope decision (single issue / epic + issues / dependencies). This is pure thinking content; it is **not** an issue body and does not carry section headings, frontmatter, workflow recommendations, or risk ratings.

Branch on the scope decision:

- **Single issue:** present the clarified requirement (the three lenses' answers + non-goals + acceptance intent) to the user for confirmation.
- **Epic + issues:** present the epic milestone (Goal / Background / Non-goals / Scope) plus one clarification per child issue, each scoped to its own context + concern and opening with its one-sentence standalone value from the Scope gate. Include the dependency list and suggested start order from the Scope stage so the epic can be advanced one issue at a time.

Present the assembled output to the user for confirmation before anything is created.

## Iteration

The lenses are serial, but not one-way. When a later lens reveals a gap in an earlier one, you may go back — but you must **say so explicitly**:

- "Thinking through the Domain Model surfaced a constraint that Product Shape missed. Going back to revise Product Shape: …"

Never silently rewrite an earlier lens's conclusions. The user must always know which lens is currently active and why a previous decision is being revisited.

Common backtrack triggers:
- Domain Model finds Product Shape is infeasible → revise Product Shape (and check User Voice still holds).
- Product Shape realizes User Voice was actually two needs → split or revise User Voice.
- User feedback on the converged clarification challenges a specific lens → revise only that lens, then re-check dependents.

## Boundaries

- Do not define issue-body sections or their writing rules. Section structure and per-section guidance live in the issue templates, applied by `mohist-create-issue`.
- Do not include issue-creation execution details (frontmatter, `mo issue create`, workflow ids, risk fields). That is the `mohist-create-issue` skill's responsibility.
- Do not prescribe implementation (files, functions, tables, task breakdown). That is the Plan stage's responsibility.
- Do not start from a blank slate and patrol for random problems. Always begin from user-provided requirement input.
- Do not let the Domain Model lens grow into a technical design document. Keep it to the minimum domain context needed to understand the requirement, or skip it.

## Handoff

When the output is confirmed, point the user to the create skills — they own every execution detail:

- **Single issue:** the `mohist-create-issue` skill picks a template (`mo issue template list`/`view`), fills its sections from this clarification, adds frontmatter, recommends workflow/risk, classifies with labels, and runs `mo issue create` after confirmation.
- **Epic + issues:** point the user to `mohist-create-epic` for the epic (`mo epic create`, link, prerequisites, lifecycle) and to `mohist-create-issue` for each child issue. The epic milestone and each issue clarification become the content the create skills fill into their templates.

In both cases this skill produces only the **clarified thinking**; the create skills own the templates, the frontmatter, and the CLI execution.
