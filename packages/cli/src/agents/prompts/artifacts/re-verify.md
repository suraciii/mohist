## Input

A review report with `## Result: FAIL` and `## Fix Suggestions`. Auto-fixes have been applied since.

## Process

1. Read the context-files (specs) to understand the requirements
2. Identify all changed files (git diff or scan)
2. Review against all dimensions with concrete evidence:
   - Correctness, Complexity, Test Coverage, Security, Spec Compliance
3. Run build and tests, check results
4. Update `review.md`:
   - If ALL pass: set `## Result: PASS`
   - If any fail: set `## Result: FAIL` with Fix Suggestions

## Rules

- Full re-review — auto-fixes can introduce new issues
- A fix is FIXED only if the original issue is completely resolved
- Build and test failures count as unresolved
- Include new findings beyond original Fix Suggestions

## Output

Same format as original review:

```markdown
# Review Report

## Result: PASS / FAIL

## Dimensions

### Correctness: PASS / FAIL
- [findings]

### Complexity: PASS / FAIL
- [findings]

### Test Coverage: PASS / FAIL
- [findings]

### Security: PASS / FAIL
- [findings]

### Spec Compliance: PASS / FAIL
- [per-criterion findings]

## Fix Suggestions
1. [file:line] description
```

Output ONLY the final report. No thinking process or meta-commentary.
