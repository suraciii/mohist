# Re-Verify

You are re-verifying specific issues that were previously flagged in a code review. Auto-fixes have been applied and you must confirm whether each Fix Suggestion is now resolved.

## Input

You are given a review report (`review.md` in the change directory) with a `## Verdict: FAIL` and a `## Fix Suggestions` section. Auto-fixes have been applied to the codebase since this report was written.

## Process

1. Read `{changeDir}/review.md` and extract all Fix Suggestions
2. For each Fix Suggestion:
   a. Read the referenced file at the specified line
   b. Determine if the issue has been resolved by the auto-fix
   c. If resolved: mark as FIXED
   d. If not resolved or partially resolved: describe what remains
3. Run the project's build command (e.g. `npm run build`) and check the result
4. Run the project's test command (e.g. `npm test`) and check the result
5. Update `{changeDir}/review.md`:
   - If ALL suggestions are resolved AND build passes AND tests pass:
     Set `## Verdict: PASS` and remove or annotate the Fix Suggestions section
   - If any suggestion remains unresolved OR build/tests fail:
     Keep `## Verdict: FAIL` and update Fix Suggestions with remaining issues

## Rules

- Verify ONLY the specific Fix Suggestions — do not perform a full re-review
- Be strict: a fix is FIXED only if the original issue is completely resolved
- Build and test failures count as unresolved issues
- Do NOT introduce new review dimensions or findings beyond the original suggestions

## Output

Produce the updated `review.md` file. The file must follow the same format as the original review:

```markdown
# Review Report

## Verdict: PASS / FAIL

## Dimensions

### Correctness: PASS / FAIL
- [findings]

### Complexity: PASS / FAIL
- [findings]

### Test Coverage: PASS / FAIL
- [findings]

### Security: PASS / FAIL
- [findings]

## Fix Suggestions
1. [file:line] description of remaining fix (only if verdict is FAIL)
```

Output ONLY the final review report. Do NOT include thinking process, reasoning, or meta-commentary.
