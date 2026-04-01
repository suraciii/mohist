---
description: Create an agentic test plan for a feature
---

Create an agentic test plan under `test/agentic/verify-<feature>/`.

**Input**: The argument after the command is either a feature name (kebab-case) or a description of what to test.

---

## Steps

1. **Determine feature name**

   If `{{input}}` is a description, derive a kebab-case name (e.g. "project CRUD" → `project-crud`).
   If `{{input}}` is empty, ask the user what feature they want to test.

2. **Understand the feature**

   Read the codebase to understand:
   - Which source files implement the feature
   - What API endpoints, CLI commands, or internal flows are involved
   - What dependencies exist (database, server, external services)

   Focus on: `packages/cli/src/` for implementation, `packages/cli/src/api/` for routes, `packages/cli/src/cli/commands/` for CLI.

3. **Create the test directory**

   ```bash
   mkdir -p test/agentic/verify-<feature>/scripts
   ```

4. **Write TESTPLAN.md**

   Follow the convention in `test/agentic/README.md`:
   - Natural language, phased (Phase 1, Phase 2, ...)
   - Each phase describes steps and expected results
   - Simple commands written in natural language — the agent will execute them directly
   - Complex deterministic operations use `@scripts/<name>.sh` references
   - End with a "collect results" section

   Reference `test/agentic/verify-m1-infra/TESTPLAN.md` as an example.

5. **Write helper scripts** (if needed)

   For each complex operation identified in the test plan:
   - Create `scripts/<name>.sh` — one script, one job
   - Each script: idempotent, clear exit code (0=ok, 1=fail), concise output
   - Make executable

6. **Verify**

   - Ensure all files exist: `TESTPLAN.md`, any `scripts/*.sh`
   - Scripts are executable
   - TESTPLAN.md phases cover the feature end-to-end

---

## Output

Summarize:
- Feature name and test directory location
- Number of phases and what each covers
- List of helper scripts created (if any)
- How to run: `/test-run <feature-name>`
