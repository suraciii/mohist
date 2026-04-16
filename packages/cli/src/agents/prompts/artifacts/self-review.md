# Self-Review

Review all generated artifacts for completeness, consistency, and feasibility.

## What to Review

Read all generated files in `{changeDir}/`:
- `proposal.md`
- `specs/**/*.md`
- `design.md` (if it exists)
- `prd.json`

## Review Criteria

### Completeness
- All requirements from the issue are covered by specs
- All specs have corresponding tasks in prd.json
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

### Quality
- Specs use SHALL/MUST language, not should/may
- Scenarios use exact `####` heading format
- Tasks have verifiable acceptance criteria
- prd.json includes mode, type, output, dependsOn fields

## Actions

If you find issues:
1. Fix them directly in the affected files
2. Ensure fixes don't break consistency with other artifacts
3. Do NOT delete artifacts — only fix in place

If all artifacts pass review, do nothing further.
