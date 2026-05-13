## Why

Mohist currently ships a single PM-style issue body template inside the `mohist` skill, which works for feature and bug work but breaks down for refactor, docs, design, and UI-heavy issues that need different kinds of context. This change is needed now because the skill is becoming bloated, technical issues are being forced into the wrong shape, and agents need clearer, label-specific instructions to produce consistent plans and implementations.

## What Changes

- Add a shared `issue-templates.md` artifact that defines separate issue authoring templates for `bug`/`feature`/`improvement`, `refactor`, `design`, `docs`, and UI-focused labels.
- Add a `mo instructions` CLI entry point that lists available templates and prints the current template for a specific label.
- Update the `mohist` shared skill so it keeps only high-level operating guidance and points users and agents to `mo instructions <label>` for the full issue template.
- Require UI-oriented issue templates to include an ASCII prototype section so layout, key elements, and interaction states are explicit before implementation starts.
- Update shared skill installation so Mohist deploys both the skill files and the standalone template artifact into the target skill directory without regressing existing `mohist` and `mohist-explore` installs.
- Align the shared skill file structure and frontmatter with the AgentSkills specification.

## Capabilities

### New Capabilities

- issue-authoring-guidance

### Modified Capabilities

- cli-interface

## Impact

- Affects `packages/cli/src/cli/index.ts` and new or updated CLI command modules for `mo instructions` output and help text.
- Affects `packages/cli/src/agent-skills/shared-agent-skills.ts`, the shared skill template source files under `packages/cli/src/agent-skills/templates/`, and a new `packages/cli/src/agent-skills/issue-templates.md` artifact used at install time.
- Likely adds parsing or lookup utilities for mapping issue labels to template sections and rendering them for terminal output.
- Requires tests under `packages/cli/tests` covering template listing, label-specific template output, UI template content, and skill installation of both `SKILL.md` and template companion files.
- Does not require database schema changes or server API changes, but it does change the user-facing contract for how Mohist issue bodies are authored and how shared skills expose that guidance.
