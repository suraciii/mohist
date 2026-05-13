## Context

Mohist currently embeds a single PM-oriented issue body template directly inside `packages/cli/src/agent-skills/templates/mohist.md`. That makes the shared `mohist` skill both the delivery mechanism and the source of truth for issue authoring guidance. The current arrangement has three concrete problems:

- `refactor` issues are forced into a user-story shape that does not fit architecture-only work.
- UI work has no standard place to describe layout and interaction structure before implementation.
- The shared skill installer only knows how to copy one markdown file per skill into `SKILL.md`, so guidance cannot be shipped as a separate artifact.

This change affects only local CLI and shared skill packaging. There are no database or server API changes, but the CLI surface and the installed skill contents become part of the user-facing contract. The design should keep existing `mohist` and `mohist-explore` installation behavior intact while moving issue templates out of the skill body.

## Goals / Non-Goals

**Goals:**

- Make label-specific issue guidance a standalone artifact that can be read by both the CLI and installed skills.
- Add a `mo instructions` command that lists available templates and prints the current template for a requested label.
- Reduce `mohist` skill size so it keeps workflow guidance, label selection guidance, and command references, while delegating full issue templates to `mo instructions`.
- Add an explicit ASCII prototype requirement for UI-oriented issue templates.
- Update shared skill installation to deploy companion files in addition to `SKILL.md` without breaking existing shared skills.
- Align the installed `mohist` skill structure with AgentSkills conventions so the skill remains portable and self-describing.

**Non-Goals:**

- Changing issue storage, labels, workflow stages, or server-side issue validation.
- Generating issue bodies automatically from labels; this change only exposes templates and instructions.
- Introducing remote template updates or a new configuration system for per-project custom templates.
- Enforcing ASCII prototype presence at issue creation time in this change unless existing specs later require hard validation.

## Decisions

### D1: Make `issue-templates.md` the single source of truth for issue authoring guidance

Create `packages/cli/src/agent-skills/issue-templates.md` as a standalone markdown artifact owned by the CLI package rather than by a single skill template. The file will contain five named template sections covering:

- user-story template for `bug`, `feature`, and `improvement`
- technical refactor template for `refactor`
- design exploration template for `design`
- documentation template for `docs`
- UI template for `ui-feature` and `ui-improvement`, including an ASCII prototype section

The file should be written for human readability first, but with a simple machine-readable structure so the CLI can list templates and extract one section deterministically. The simplest structure is top-level sections with stable identifiers in headings, for example `## Template: refactor`, followed by a short metadata line such as `Labels: refactor`. This keeps the artifact editable as markdown without introducing a separate data format.

Keeping all template content in one file ensures the CLI, installed skills, and future docs all read the same text. Updating template wording then becomes a source change in one place, rather than duplicated edits in skill markdown and command help.

**Alternatives considered:**

- Store templates as a TypeScript object and generate markdown on demand. Rejected because it makes the authored content harder to review and less useful as a shipped companion file.
- Split each template into its own markdown file. Rejected because the listing and deployment story becomes more fragmented, while the expected template count is still small.
- Keep templates embedded in `mohist.md` and parse that file. Rejected because it preserves the current coupling and does not make the skill thinner.

### D2: Implement `mo instructions` as a local markdown lookup command, not a server-backed feature

Add a new top-level CLI command module, registered from `packages/cli/src/cli/index.ts`, with the following behavior:

- `mo instructions` prints the available template groups and the labels each one serves.
- `mo instructions <label>` normalizes the label, maps it to a template section, and prints the corresponding markdown body.
- Unknown labels return a non-zero exit path with a message that also shows valid labels.

The command should read `issue-templates.md` directly from the package source/runtime location, then use a small parser utility in `packages/cli/src/agent-skills/` or `packages/cli/src/cli/` to extract sections by label. The lookup layer should separate:

- label aliasing, such as `feature` and `bug` both resolving to the shared user-story template
- content extraction from the markdown artifact

That separation keeps the parser dumb and the command behavior explicit. It also avoids inventing label-specific duplicated sections just to satisfy CLI lookup.

This command should remain completely local and should not call `requireServer()`, because the template content is static package data and should be available even when Mohist server is stopped.

**Alternatives considered:**

- Add `instructions` under `mo skills`. Rejected because issue authoring guidance is a general CLI affordance, not only a skill-management concern.
- Serve templates from the server API. Rejected because it adds unnecessary runtime dependency and does not help the installed skill use case.
- Hardcode the template output in the command itself. Rejected because it recreates the duplication this change is trying to remove.

### D3: Keep the `mohist` skill thin and point it at the command plus the shipped artifact

Revise `packages/cli/src/agent-skills/templates/mohist.md` so it retains:

- command reference for common `mo` operations
- issue label and priority guidance
- brief instructions for choosing a template by label
- explicit direction to run `mo instructions <label>` before authoring or revising an issue body

Remove the embedded full issue template examples from the skill body. The installed skill should still be useful in isolation by mentioning where the companion template artifact is installed and by describing the selection rules, but it should not duplicate the full template text.

For AgentSkills compatibility, the frontmatter should stay minimal and specification-aligned: stable `name`, concise `description`, and body content structured as executable guidance rather than repository-specific scaffolding. This change does not require inventing custom frontmatter fields unless the specification already expects them.

**Alternatives considered:**

- Remove all issue creation guidance from the skill and rely entirely on CLI discovery. Rejected because the skill still needs to tell an agent when and how to ask for the right template.
- Keep one abbreviated template inline for fallback. Rejected because even a shortened copy creates drift pressure and weakens the single-source-of-truth goal.

### D4: Extend shared skill installation from “one markdown file per skill” to “skill bundles”

The current `installSharedAgentSkills()` implementation treats every markdown file in `packages/cli/src/agent-skills/templates/` as a skill and copies it to `<skill>/SKILL.md`. That model cannot ship companion artifacts like `issue-templates.md`.

The installer should be refactored around an explicit manifest of shared skill bundles. Each bundle defines:

- skill name
- source `SKILL.md` template
- optional extra files to copy into the same installed directory

Under this model:

- `mohist` installs `SKILL.md` plus `issue-templates.md`
- `mohist-explore` continues to install only `SKILL.md`

The `mo skills list` command should continue to list shared skill names, not companion files. The `mo skills install` output can still report per-skill install status, while writing all bundle files behind the scenes.

This keeps compatibility with current UX and avoids misclassifying `issue-templates.md` itself as a skill. It also creates a clean path for future shared skills that need references, examples, or prompt fragments alongside `SKILL.md`.

**Alternatives considered:**

- Keep directory scanning and special-case `issue-templates.md` by filename. Rejected because it encodes a one-off exception into logic that is already too implicit.
- Move every skill into its own source directory immediately. Rejected as a larger migration than needed for this change, though the manifest design leaves that option open later.

### D5: Encode the UI prototype requirement in the template contract, not in bespoke CLI logic

UI-oriented labels should resolve to a dedicated template whose acceptance guidance explicitly requires an ASCII prototype section with layout boxes, key elements, and interaction/state notes. This requirement belongs in the authored template because it changes the quality of issue descriptions, not the mechanics of the CLI.

The template should include:

- a required `ASCII 原型图` or equivalent section heading
- a minimal example showing box layout
- instructions for multi-frame sketches when a state transition matters

By putting this in the template text, both human users and agents receive the same expectation wherever they consume the instructions. Tests should verify that the rendered UI template contains the prototype requirement and example content.

**Alternatives considered:**

- Add a separate warning in `mo issue create` for UI labels. Rejected for now because the proposal only requires guidance, and creation-time validation would be a separate product decision.
- Reuse the general user-story template and append one sentence about prototypes. Rejected because UI work needs its own shape and examples to be effective.

## Risks / Trade-offs

- [Markdown section parsing becomes brittle if headings are edited casually] → Use a tiny documented section format with tests that fail when required headings or label metadata disappear.
- [Installed skill and local CLI may drift if one reads a different source copy] → Have both CLI output and installer copy from the same repository artifact, not duplicated strings.
- [Users may expect `mo instructions` to support arbitrary labels] → Keep the label mapping explicit and print valid labels in the error output.
- [Thinner skill may be less immediately informative in environments where `mo` is unavailable] → Keep selection guidance and command examples in the skill, and install `issue-templates.md` beside `SKILL.md` for local reference.
- [Manifest-based installer adds a small amount of structure compared with directory scanning] → Limit the manifest to the shared skills that already exist and keep bundle metadata minimal.

## Migration Plan

1. Add `issue-templates.md` with stable template section markers and label mappings.
2. Add a small template lookup utility and the `mo instructions` CLI command, then register it in `packages/cli/src/cli/index.ts`.
3. Rewrite `templates/mohist.md` to reference `mo instructions <label>` and remove embedded full templates while preserving command guidance.
4. Refactor `shared-agent-skills.ts` to install explicit skill bundles and copy companion files for `mohist`.
5. Add tests covering template listing, label lookup, UI template content, and multi-file skill installation.
6. Install skills in a test fixture or temporary directory to verify `mohist/SKILL.md`, `mohist/issue-templates.md`, and `mohist-explore/SKILL.md` all land in the correct place.

Rollback is straightforward: revert the new command and installer changes, restore the old `mohist` embedded template, and remove the standalone artifact. No persisted data migration is involved.

## Open Questions

- Should UI labels be standardized as exactly `ui-feature` and `ui-improvement`, or should the command also accept aliases such as `ui`? The implementation can support aliases, but the accepted label vocabulary should stay consistent with the rest of the product.
- Should `mo instructions` print raw markdown only, or include a short header line such as `Template for label: refactor`? Raw markdown is easier to pipe into other tools, but a small header may help terminal readability.
