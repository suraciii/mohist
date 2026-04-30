## Input

A review report with `## Result: FAIL` and `## Fix Suggestions` listing file paths and line numbers.

## Process

1. Read the context-files to understand the change intent (proposal), approach (design), and requirements (specs)
2. Read the review report and extract all Fix Suggestions
3. For each: read the file, understand the issue, apply the minimal fix
4. After all fixes, add/update tests for the fixed behavior
5. Verify build and tests pass

## Rules

- Apply ONLY the fixes described — no speculative changes
- If ambiguous, apply the most conservative interpretation
- If a fix requires multiple files, make all changes
- If a fix cannot be safely applied, skip it and note why
- Each fix must be atomic — do not bundle unrelated fixes
- New tests must focus on the specific fix

## Output

```
## Applied Fixes
1. [file:line] What was changed and why

## Skipped Fixes
1. [file:line] Why it was skipped

## Build Result
- Build: PASS / FAIL
- Tests: PASS / FAIL
```

Do NOT modify `review.md`.
