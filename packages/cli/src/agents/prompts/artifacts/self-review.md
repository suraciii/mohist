# Self-Review

Review all generated artifacts for completeness, consistency, and feasibility.

## What to Review

Read all generated files in `{changeDir}/`:
- `proposal.md`
- `specs/**/*.md`
- `design.md`
- `tasks.json`

## Review Criteria

### Completeness
- All requirements from the issue are covered by specs
- All specs have corresponding tasks in tasks.json
- All edge cases are considered
- No requirement is left unaddressed

### Consistency
- Specs align with the proposal's Capabilities section
- Tasks reference the correct spec files
- Design decisions align with spec requirements
- Naming is consistent across all artifacts

### Feasibility
- All dependencies are available or created by earlier tasks
- No circular dependencies in task graph
- Implementation steps are clear and actionable
- Task granularity is appropriate (each completable in one agent iteration)

### Dependency Completeness
- Every non-first task (priority > 1) has at least one `dependsOn` entry
- All `dependsOn` references point to existing task IDs with lower priority numbers
- The dependency graph contains no cycles
- Dependencies reflect actual input/output relationships between tasks

### Quality
- Specs use SHALL/MUST language, not should/may
- Scenarios use exact `####` heading format
- Tasks have verifiable acceptance criteria
- tasks.json includes mode, type, output, dependsOn fields

## Actions

If you find issues:
1. Fix them directly in the affected files
2. Ensure fixes don't break consistency with other artifacts
3. Do NOT delete artifacts — only fix in place

If all artifacts pass review, do nothing further.

## Output Format

Write a self-review summary to `{changeDir}/self-review.md` with:

```markdown
# Self-Review Report

## Result: PASS / FAIL

## Completeness: PASS / FAIL
- [findings]

## Consistency: PASS / FAIL
- [findings]

## Feasibility: PASS / FAIL
- [findings]

## Dependency Completeness: PASS / FAIL
- [findings]

## Quality: PASS / FAIL
- [findings]

## Fixes Applied
1. [description of each fix applied, or "None" if all passed]
```
