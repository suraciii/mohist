## Context

The review pipeline has three quality gates that are all currently bypassed:

1. **Spec compliance** — `buildReviewerPrompt` (`artifact-prompt.ts:136-154`) only injects issue info + review instruction. It doesn't read the change's `specs/` directory or `tasks.json`, so the review agent cannot check implementation against specifications.

2. **AC verification** — `ralph-executor.ts:470` checks `result.success` (did the ACP session exit cleanly?) but never validates whether acceptance criteria are actually met. The AC list in `context-assembler.ts:122-128` is rendered as display-only `- [ ]` items.

3. **tasks.json persistence** — `writeTasksFile` at `ralph-executor.ts:472-473` writes tasks.json *after* the agent has committed its code. Then `mergeBack` at `worktree-manager.ts:187` runs `git add :!openspec/changes/` which excludes the entire `openspec/changes/` directory, so the last task's `passes=true` update is always lost during merge.

Additionally, `review-self-check.md` only validates report *format* (heading, Verdict section, no placeholders), not *content correctness*. And `packages/cli/web/` has vitest configured but zero utility-function tests — pure functions like `formatTime`, `formatTimeAgo`, and `statusBadge` are untested despite having caused regressions in Issue #30.

## Goals / Non-Goals

**Goals:**
- Fix all three quality gates so they actually gate
- Ensure tasks.json updates survive mergeBack in 100% of cases
- Add Spec Compliance as a first-class review dimension
- Add unit tests for web utility functions to catch exact-value regressions

**Non-Goals:**
- Refactoring the review agent into a separate microservice
- Building a formal AC verification DSL or parser — we use LLM-based verification (spawn a second agent call)
- Adding E2E/integration tests for the full pipeline (separate concern)
- Changing the mergeBack git strategy (rebase vs merge) — we only fix the pathspec exclusion

## Decisions

### D1: Fix tasks.json sync by removing the `openspec/changes/` exclusion from mergeBack

Change `git add -- :!openspec/changes/ :!.opencode/` to `git add -- :!.opencode/` in `worktree-manager.ts:187`. The `openspec/changes/` exclusion was originally intended to avoid committing internal pipeline state, but it causes data loss for tasks.json. The ralph executor will also commit tasks.json after each update (belt-and-suspenders), but removing the exclusion is the primary fix.

**Why not keep the exclusion and only add a ralph commit?** Because the exclusion is the root cause. If ralph commits tasks.json but mergeBack re-stages everything *except* `openspec/changes/`, any uncommitted tasks.json edits (e.g. from a race condition or failed ralph commit) are still lost. Removing the exclusion is simpler and more reliable.

**Alternatives considered:**
- Keep exclusion, only add ralph commit: Still risky if commit fails or is missed
- Move tasks.json out of `openspec/changes/`: Breaks existing convention and all code that reads it

### D2: Inject specs and tasks.json into `buildReviewerPrompt` via a helper function

Add a `loadSpecContext(changeDir: string): string` helper to `artifact-prompt.ts` that:
1. Reads all `*.md` files under `{changeDir}/specs/` recursively
2. Reads `{changeDir}/tasks.json`
3. Returns a formatted string with `## Specs` and `## Tasks & Acceptance Criteria` sections

Insert this into the prompt parts array between "Change Directory" and "Goal". If `specs/` doesn't exist, the section is omitted (graceful degradation).

**Why not pass specs as a separate parameter?** The `buildReviewerPrompt` signature is `(issue, changeDir)`. Adding a new parameter would require changing all call sites. Reading from `changeDir` is consistent with how the function already works — it's a path-based context lookup.

**Alternatives considered:**
- Add `specs` parameter: Breaks call sites, unnecessary coupling
- Read specs in the prompt template (review.md): The agent can't read files that aren't in its prompt context

### D3: Add Spec Compliance dimension to review.md

Add a new `### Spec Compliance` section to `review.md` between Security and Review Process, with instructions to:
- Check each acceptance criterion from tasks.json against the implementation
- Verify exact values (colors, strings, formats, constants)
- Report per-criterion pass/fail

Update the Output Format template to include the new dimension.

### D4: Enhance review-self-check.md to validate AC coverage

Add a new "Spec Compliance Coverage" section to `review-self-check.md` that checks:
- Report has a `### Spec Compliance` section
- Each AC from tasks.json is explicitly addressed
- Findings are specific (not generic "code looks correct")

### D5: Extract utility functions for testing, don't move them

The utility functions (`formatTime`, `formatTimeAgo`, `statusBadge`, `LEVEL_COLORS`) are currently private to their component files. Rather than moving them (which would change imports across many components), we will:
1. Create `src/lib/format-time.ts` and `src/lib/status-badge.ts` with the extracted functions
2. Update components to import from the new modules
3. Write tests against the new modules

This is the standard pattern for making component-internal functions testable.

### D6: AC verification via LLM (second agent call) — deferred to separate change

The spec calls for running an AC verification step after each task completes. This requires spawning a second ACP session per task to check AC satisfaction, which is a significant runtime cost and architectural change. We will implement the infrastructure (the verification step in the ralph loop) but use a simpler approach: include AC verification instructions in the coder's post-task instruction block, and log verification results. Full LLM-based AC verification will be a follow-up change.

## Risks / Trade-offs

- **[Risk: mergeBack now includes openspec/changes/ files]** → The `:!.opencode/` exclusion still prevents opencode config from leaking. The only additional files are tasks.json, session-memories, and any uncommitted spec edits — all of which are useful to preserve. No secrets or sensitive data is stored in `openspec/changes/`.

- **[Risk: spec context bloats review prompt]** → Specs are typically 50-200 lines per file. With 3-5 specs, that's 150-1000 lines. This is well within LLM context windows. If it becomes an issue, we can truncate or summarize, but this is unlikely.

- **[Risk: Review agent may not follow spec compliance instructions perfectly]** → The self-check step catches this. The self-check now verifies AC coverage explicitly.

- **[Risk: Utility function extraction may break components]** → Simple import changes, easily caught by existing component tests and TypeScript compiler.

## Migration Plan

1. Deploy `worktree-manager.ts` fix first — this is the most impactful bug (100% reproduction rate). Verify by checking tasks.json in a completed issue's merge.
2. Deploy `artifact-prompt.ts` + `review.md` + `review-self-check.md` changes — these only affect the review stage, which runs on new issues. No migration needed.
3. Deploy `ralph-executor.ts` tasks.json commit — additive change, no migration.
4. Deploy web utility tests — no migration, just new test files.

No rollback concerns: all changes are additive or fix bugs. The mergeBack pathspec change is the only behavioral modification, and its rollback is a single line revert.

## Open Questions

None — all decisions are resolved.
