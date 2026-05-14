## Findings

1. Error: Hermes install can still copy discovery stubs instead of packaged `skill-data`.
File: `packages/cli/src/agent-skills/shared-agent-skills.ts:101-103`
File: `packages/cli/src/agent-skills/skill-data-service.ts:153-159`
`installHermesSkills()` calls `skillService.resolveSkillPath(skillName)`, but `resolveSkillPath()` falls back to `stubs/<name>` whenever `skill-data/<name>` is missing. That means a packaging slip or partial install would silently install the stub `SKILL.md` into Hermes, violating the spec requirements that Hermes installs must come from `skill-data/` and must not copy discovery stubs. The current happy-path tests pass because `skill-data/` exists in this worktree, but the implementation does not enforce the required source.

Suggested fix:
File: `packages/cli/src/agent-skills/skill-data-service.ts:153-159`
Change `resolveSkillPath()` or add a new dedicated resolver so Hermes uses only `skill-data/<name>` and throws if that directory is missing.
File: `packages/cli/src/agent-skills/shared-agent-skills.ts:101-103`
Replace the silent `continue` path with a hard failure so `mo skills install --hermes` cannot succeed with incomplete or stub-backed installs.

## Spec Compliance

1. FAIL - Hermes install copies full packaged guidance
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:101-112` copies whatever `resolveSkillPath()` returns; `packages/cli/src/agent-skills/skill-data-service.ts:153-159` can return `stubs/<name>` when `skill-data/<name>` is absent. Current tests show the happy path works (`packages/cli/tests/shared-agent-skills.test.ts:179-199`, `packages/cli/tests/skill-dynamic-loading.test.ts:325-350`), but the implementation does not guarantee the required source.

2. FAIL - Hermes install does not copy discovery stubs
Evidence: same fallback path as above in `packages/cli/src/agent-skills/skill-data-service.ts:153-159`. The current packaged tree avoids the bug today, but the code path still permits stub installation.

3. PASS - Hermes install is limited to Mohist built-in skills
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:31,101-118` installs only `mohist` and `mohist-explore`; regression tests verify `mohist-po` is not installed in `packages/cli/tests/shared-agent-skills.test.ts:209-214` and `packages/cli/tests/skill-dynamic-loading.test.ts:353-362`.

4. PASS - Hermes install respects Hermes native home
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:72-74,95-96` resolves `${HERMES_HOME:-~/.hermes}/skills`; tests cover both explicit `hermesHome` and `HERMES_HOME` env usage in `packages/cli/tests/shared-agent-skills.test.ts:150-177,271-282`.

5. PASS - Hermes install leaves external dirs config untouched
Evidence: Hermes install logic is confined to `packages/cli/src/agent-skills/shared-agent-skills.ts:94-120` and `packages/cli/src/cli/commands/skills.ts:63-69`; there is no code reading or writing Hermes `config.yaml` or `skills.external_dirs`.

6. PASS - Hermes install reports repeatable results and usage
Evidence: created/updated detection is implemented in `packages/cli/src/agent-skills/shared-agent-skills.ts:106-117`; CLI usage and reload guidance are printed in `packages/cli/src/cli/commands/skills.ts:31-36,66-68`; tests cover created/updated behavior in `packages/cli/tests/shared-agent-skills.test.ts:216-235`.

7. PASS - Existing repository and Claude installs remain unchanged
Evidence: repository/Claude stub installer remains in `packages/cli/src/agent-skills/shared-agent-skills.ts:48-69`; `--claude` and `--path` behavior remains in `packages/cli/src/cli/commands/skills.ts:72-79`; regression coverage exists in `packages/cli/tests/shared-agent-skills.test.ts:94-127,237-255,296-311` and `packages/cli/tests/skill-dynamic-loading.test.ts:153-188,374-398`.

## Quality Checks

1. Complexity: PASS
Evidence: new functions are small and straightforward; `installHermesSkills()` is 27 lines and `copyDirRecursive()` is 13 lines.

2. Test coverage: PASS with gap
Evidence: `npx vitest run tests/shared-agent-skills.test.ts tests/skill-dynamic-loading.test.ts` passed with 71/71 tests, and `npm run build` passed. Gap: there is no regression test for the missing-`skill-data` failure mode that currently allows stub fallback.

3. Security: PASS
Evidence: no shell execution or config mutation was added; filesystem writes are limited to the resolved Hermes skills directory.

<promise>FAIL</promise>
