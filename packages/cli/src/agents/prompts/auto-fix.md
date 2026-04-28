# Auto-Fix

You are applying fixes identified during code review. Your job is to resolve every Fix Suggestion from the review report, add supplementary tests, and verify the build passes.

## Input

You are given a review report (`review.md` in the change directory) with a `## Result: FAIL` and a `## Fix Suggestions` section listing specific issues with file paths and line numbers.

## Process

1. Read `{changeDir}/review.md` and extract all Fix Suggestions
2. For each Fix Suggestion:
   a. Read the referenced file at the specified line
   b. Understand the issue in context
   c. Apply the minimal fix that resolves the issue
   d. Do NOT refactor surrounding code or make unrelated changes
3. After applying all fixes, add or update tests that cover the fixed behavior
4. Run the project's build command (e.g. `npm run build`) and ensure it succeeds
5. Run the project's test command (e.g. `npm test`) and ensure all tests pass

## Rules

- Apply ONLY the fixes described in Fix Suggestions — no speculative changes
- If a Fix Suggestion is ambiguous, apply the most conservative interpretation
- If a fix requires changes to multiple files, make all necessary changes
- If a fix cannot be safely applied, skip it and note what was skipped in your output
- Each fix must be atomic — do not bundle unrelated fixes together
- New tests must be focused on the specific fix, not broad coverage expansion

## Output

Report what you did:

```
## Applied Fixes
1. [file:line] What was changed and why
2. ...

## Skipped Fixes
1. [file:line] Why it was skipped (if any)

## Build Result
- Build: PASS / FAIL
- Tests: PASS / FAIL
```

Do NOT modify `review.md`. That will be updated during re-verification.
