# Review: Issue #191 — Improve mohist issue template system

## Summary

The implementation adds a multi-template issue authoring system with a `mo instructions` CLI command, thins the `mohist` skill to delegate template content to the command, and refactors shared skill installation to support companion files. All 22 tests pass. Build compiles cleanly.

## Acceptance Criteria

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | `issue-templates.md` exists with 5 templates (including UI prototype) | **PASS** | `packages/cli/src/agent-skills/issue-templates.md` contains `## Template: user-story` (line 5), `## Template: refactor` (line 36), `## Template: design` (line 62), `## Template: docs` (line 97), `## Template: ui` (line 123) |
| 2 | `mo instructions` lists templates and outputs per-label content | **PASS** | `packages/cli/src/cli/commands/instructions.ts` registers the command; `mo instructions` (no label) calls `listTemplates()`, `mo instructions <label>` calls `printTemplateForLabel()`; unknown labels exit with non-zero code |
| 3 | `mohist` skill file conforms to agentskills.io specification | **PASS** | Frontmatter at `templates/mohist.md:1-4` has `name: mohist` (lowercase, hyphenated, matches directory) and `description` field; both required fields present; body under 500 lines (118 lines) |
| 4 | refactor issues can use technical template without user stories | **PASS** | `issue-templates.md:36-60` — refactor template has sections for 重构目标, 当前状态, 重构范围, 验收标准, 技术注意事项; no user story section |
| 5 | UI templates require ASCII prototype diagrams | **PASS** | `issue-templates.md:132-183` — `## ASCII 原型图` section with box layout example (`+---+` boxes), multi-frame state change examples, and explicit instructions |
| 6 | `mo skills install` deploys skill and template files | **PASS** | `shared-agent-skills.ts:20-31` — `SHARED_SKILL_BUNDLES` manifest installs `issue-templates.md` as extra file for `mohist`; `build:backend` script copies it to `dist/agent-skills/` |
| 7 | Existing behavior not regressed | **PASS** | Tests confirm `mohist-explore` installs only `SKILL.md` (no `issue-templates.md`); `getSharedSkillNames()` returns only `['mohist', 'mohist-explore']` |

## Correctness

- **Template parsing** (`issue-template-lookup.ts:42-66`): Splits on `## Template: ` prefix, collects content between template headings. Correct and handles the trailing template properly (line 60-63).
- **Label normalization** (`issue-template-lookup.ts:18`): `toLowerCase().trim()` handles case/whitespace input. Tested at `shared-agent-skills.test.ts:316`.
- **Error handling** (`instructions.ts:40-48`): Unknown label prints valid labels and returns `false`, triggering `process.exit(1)`. Correct.
- **File existence check** (`issue-template-lookup.ts:81-83`): Returns `null` if template file missing. Graceful degradation.

## Complexity

All functions are well under 50 lines, cyclomatic complexity under 10. The longest function `parseTemplatesFile` is 25 lines. The lookup module is clean with good separation of concerns (label mapping vs content extraction vs file reading).

## Security

- No user input is used to construct file paths for reading. `getTemplatePath()` uses `__dirname` + fixed filename.
- No injection risks. Label input is only used as a map key lookup.

## Test Coverage

22 tests in `shared-agent-skills.test.ts` covering:
- Skill installation (create, update, overwrite, `.claude` path, `--path` option)
- Bundle companion file installation (`issue-templates.md` for mohist, not for explore)
- Template lookup (all 5 templates, label normalization, unknown label, UI ASCII content)
- CLI command registration

All 22 tests pass.

## Warnings

1. **Unrelated changes included**: `build-stage-runner.ts`, `workflow-engine.ts`, and `build-workflowrun-tasks.test.ts` have changes unrelated to issue #191 (appears to be a reverted refactor of aggregate task execution). These should be reviewed separately or removed from this branch if accidental.

2. **`mo instructions list` subcommand overlap**: `instructions.ts:20-25` registers a `list` subcommand that duplicates the no-argument behavior of `mo instructions`. This is harmless but slightly redundant — a user could run either `mo instructions` or `mo instructions list` with identical output.

3. **Template file coupling to `__dirname`**: `getTemplatePath()` resolves relative to `__dirname`, which works for both source and compiled `dist/` since the build script copies `issue-templates.md`. However, if the file is missing from the dist (e.g., custom build), the command silently returns null. Consider a more explicit error message.

<promise>PASS</promise>
