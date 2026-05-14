## Findings

1. Error: Hermes CLI output does not report literal `created` / `updated` statuses required by the spec.
File: `packages/cli/src/cli/commands/skills.ts:23-28,63-68`
Evidence: `formatHermesResult()` prints only an icon plus destination path, and the `--hermes` action only calls that formatter. The strings `created` and `updated` are never printed in the user-facing command output.
Impact: This misses the acceptance criterion "the command output includes `created`/`updated` results" even though `installHermesSkills()` returns those statuses internally.
Suggested fix: Update `formatHermesResult()` to include `r.result` in each line, for example `created mohist -> ...` / `updated mohist -> ...`, and add a CLI-level test that captures `console.log` output for first and repeated `mo skills install --hermes` runs.

## Spec Compliance

1. PASS: Hermes install copies full packaged guidance.
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:101-115` resolves `resolvePackagedSkillPath(skillName)` and recursively copies the whole packaged directory into `<hermesHome>/skills/<name>`. Tests cover `mohist/references/issue-templates.md` at `packages/cli/tests/shared-agent-skills.test.ts:179-188`.

2. PASS: Hermes install does not copy discovery stubs.
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:102-105` now throws if packaged `skill-data` is missing instead of falling back to stubs. Tests verify installed content is not stub content at `packages/cli/tests/shared-agent-skills.test.ts:190-207` and missing packaged data fails at `:209-225`.

3. PASS: Hermes install is limited to Mohist built-in skills.
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:31,101` hard-codes only `mohist` and `mohist-explore`. Tests verify `mohist-po` is not installed at `packages/cli/tests/shared-agent-skills.test.ts:227-232`.

4. PASS: Hermes install respects Hermes native home.
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:72-75,95-96` uses `HERMES_HOME` or `~/.hermes`, and tests verify env-driven/custom home behavior at `packages/cli/tests/shared-agent-skills.test.ts:162-177,289-300`.

5. PASS: Hermes install leaves external dirs config untouched.
Evidence: No code references to `config.yaml` or `external_dirs` were found in `packages/cli/src` for this change, and the implementation only performs filesystem writes under `<hermesHome>/skills` in `packages/cli/src/agent-skills/shared-agent-skills.ts:95-115`.

6. FAIL: Hermes install reports repeatable results and usage.
Evidence: Internal result objects do track `created`/`updated` in `packages/cli/src/agent-skills/shared-agent-skills.ts:108-119`, and usage/reload guidance is printed in `packages/cli/src/cli/commands/skills.ts:31-35,63-68`. However, the actual CLI output omits the literal `created` / `updated` statuses, so the command does not fully satisfy this scenario.

7. PASS: Existing repository and Claude installs remain unchanged.
Evidence: Default/Claude paths still use `installSharedAgentSkills()` in `packages/cli/src/cli/commands/skills.ts:72-79`, and Hermes mode is gated separately at `:63-70`. Tests cover `.agents` and `.claude` separation at `packages/cli/tests/shared-agent-skills.test.ts:255-273,303-343`.

8. PASS: Ambiguous target combinations fail clearly.
Evidence: `packages/cli/src/cli/commands/skills.ts:54-60` rejects `--hermes --claude` and `--hermes --path`. Tests cover both cases at `packages/cli/tests/shared-agent-skills.test.ts:357-400`.

## Quality Notes

- Complexity: The changed functions remain small; `installHermesSkills()` is 29 lines and `formatHermesResult()` is 6 lines.
- Security: No injection or secret-handling issues found. Inputs are limited to local filesystem paths.
- Test coverage: Relevant unit tests were added for packaged-skill-only Hermes installs, but I could not execute the suite in this environment because `vitest` and `tsc` are not installed (`pnpm test ...` -> `vitest: not found`, `pnpm exec tsc --noEmit` -> `Command "tsc" not found`).

<promise>FAIL</promise>
