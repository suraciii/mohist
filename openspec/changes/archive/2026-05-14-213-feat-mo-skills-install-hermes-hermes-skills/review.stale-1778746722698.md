## Findings

1. Error: Hermes CLI output does not explicitly report `created`/`updated`, so the implementation does not satisfy the repeatable-results output requirement as written in the spec.
File: `packages/cli/src/cli/commands/skills.ts:23-28`
Evidence: `formatHermesResult()` only prints an icon, skill name, and destination path: ``✓ mohist -> ...`` or ``↻ mohist -> ...``. The spec requires the command to report `created` on first install and `updated` on repeat install. There is no literal `created` or `updated` text in the user-facing output.
Suggested fix: Update `formatHermesResult()` to include the result text, for example `created mohist -> ...` and `updated mohist -> ...`, and add a CLI-output test that asserts those exact words appear for first and repeated installs.

## Spec Compliance

1. PASS: Hermes install copies full packaged guidance.
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:101-115` uses `resolvePackagedSkillPath()` and recursively copies the packaged directory into `<hermesHome>/skills/<name>`. Tests cover `mohist` and `mohist-explore` installation plus `references/issue-templates.md` at `packages/cli/tests/shared-agent-skills.test.ts:150-188` and `packages/cli/tests/skill-dynamic-loading.test.ts:325-342`.

2. PASS: Hermes install does not copy discovery stubs.
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:102-105` now throws if packaged skill-data is missing instead of falling back to stubs. Tests verify installed content is not hidden stub content and missing packaged data fails rather than using stubs at `packages/cli/tests/shared-agent-skills.test.ts:190-225`.

3. PASS: Hermes install is limited to Mohist built-in skills.
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:31,101` installs only `mohist` and `mohist-explore`. Tests verify `mohist-po` is not installed at `packages/cli/tests/shared-agent-skills.test.ts:227-232` and `packages/cli/tests/skill-dynamic-loading.test.ts:353-362`.

4. PASS: Hermes install respects Hermes native home.
Evidence: `packages/cli/src/agent-skills/shared-agent-skills.ts:72-75,95-96` resolves `HERMES_HOME` or defaults to `~/.hermes`. Tests verify env override and custom home usage at `packages/cli/tests/shared-agent-skills.test.ts:162-177,289-299`.

5. PASS: Hermes install leaves external dirs config untouched.
Evidence: No code in the changed implementation references `config.yaml` or `skills.external_dirs`; Hermes install only copies packaged directories into `<hermesHome>/skills`. Search across `packages/cli/src` found no matches for `config.yaml`, `external_dirs`, or `skills.external_dirs`.

6. FAIL: Hermes install reports repeatable results and usage.
Evidence: Internal result objects track `created`/`updated` in `packages/cli/src/agent-skills/shared-agent-skills.ts:116-119`, and CLI output includes usage examples plus reload/reset guidance in `packages/cli/src/cli/commands/skills.ts:31-35`. However, the displayed install lines in `packages/cli/src/cli/commands/skills.ts:23-28` do not print the words `created` or `updated`, only icons. This misses the spec's explicit reporting requirement.

7. PASS: Existing repository and Claude installs remain unchanged.
Evidence: Default and `--claude` paths still use `installSharedAgentSkills()` at `packages/cli/src/cli/commands/skills.ts:72-79`. Tests verify `.agents` and `.claude` behavior remains separate from Hermes at `packages/cli/tests/shared-agent-skills.test.ts:25-128,303-343`.

8. PASS: Ambiguous target option combinations fail clearly.
Evidence: `packages/cli/src/cli/commands/skills.ts:54-60` rejects `--hermes --claude` and `--hermes --path` with explicit errors. Tests cover both at `packages/cli/tests/shared-agent-skills.test.ts:357-401`.

## Quality Checks

1. Correctness: One user-visible spec gap remains in Hermes install result reporting.
2. Complexity: The touched functions remain small and simple; no complexity concern found.
3. Test Coverage: Relevant tests pass, but there is no CLI-output assertion for literal `created`/`updated` text.
4. Security: No injection or secret-handling issue found in the touched code.

## Validation

1. `npm test -- shared-agent-skills.test.ts skill-dynamic-loading.test.ts` PASS
2. `npm run build` PASS

<promise>FAIL</promise>
