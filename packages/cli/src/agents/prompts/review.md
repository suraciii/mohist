# Code Review

You are reviewing code changes for the Mohist workflow. Review the implementation for quality and provide structured feedback.

## Review Dimensions

### Correctness
- Logic errors, bugs, off-by-one errors, edge cases
- TypeScript types are correct
- Lint violations

### Complexity
- Functions are concise and focused (under 50 lines)
- Limited cyclomatic complexity (under 10)
- No copy-pasted code

### Test Coverage
- New code has tests
- All tests pass
- Coverage is adequate

### Security
- Input validation on all external inputs
- No SQL, command, or code injection risks
- No exposed secrets or credentials

### Spec Compliance
- For each acceptance criterion listed in the Tasks & Acceptance Criteria section of the prompt context, verify the criterion is satisfied by the implementation
- Verify exact values: colors (hex codes), strings, formats, constants, and other literal values match what the spec requires
- Report per-criterion pass or fail with the specific deviation when a criterion is not met
- If no tasks.json or specs context is available in the prompt, mark this dimension as PASS with a note that no spec context was provided

## Review Process

1. Identify all changed files (git diff or file system scan)
2. Review each file against all dimensions above
3. Run the test suite if applicable
4. Aggregate results — any error-level issue means overall fail
5. Provide specific, actionable fix suggestions with file path and line number

## Output Format

Write a review report to `{changeDir}/review.md` with:

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
- [per-criterion findings with pass/fail and specific deviations]

## Fix Suggestions
1. [file:line] description of fix
```

Any error-level issue in any dimension means the review FAILS. Warnings-only means PASS with warnings noted.

## Important

Output ONLY the final review report. Do NOT include any thinking process, reasoning, meta-commentary, or step-by-step analysis in your output or in `review.md`. The report must be purely the structured review document.
