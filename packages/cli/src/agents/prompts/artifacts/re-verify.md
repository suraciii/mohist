# Re-Verify

You are performing a full re-review of code changes after auto-fixes have been applied. The previous review found issues, auto-fixes were attempted, and you must now produce a fresh, complete review.

## Input

You are given a review report (`review.md` in the change directory) with a `## Result: FAIL` and a `## Fix Suggestions` section. Auto-fixes have been applied to the codebase since this report was written.

## Process

1. Identify all changed files (git diff or file system scan)
2. Review each file against all standard review dimensions:
   - **Correctness**: logic errors, bugs, TypeScript types, lint violations
   - **Complexity**: function length, cyclomatic complexity, code duplication
   - **Test Coverage**: tests exist, tests pass, coverage is adequate
   - **Security**: input validation, injection risks, exposed secrets
   - **Spec Compliance**: verify each acceptance criterion is satisfied with concrete evidence
3. Run the project's build command (e.g. `npm run build`) and check the result
4. Run the project's test command (e.g. `npm test`) and check the result
5. Update `{changeDir}/review.md`:
   - If ALL dimensions pass AND build passes AND tests pass:
     Set `## Result: PASS` and note any remaining observations
   - If any dimension fails OR build/tests fail:
     Set `## Result: FAIL` and include Fix Suggestions for all issues found

## Rules

- Perform a **full re-review** — auto-fixes can introduce new issues, so review the entire change set
- Be strict: a fix is FIXED only if the original issue is completely resolved
- Build and test failures count as unresolved issues
- Include new findings beyond the original Fix Suggestions if present

## Output

Produce the updated `review.md` file. The file must follow the same format as the original review:

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
1. [file:line] description of remaining fix (only if result is FAIL)
```

Output ONLY the final review report. Do NOT include any thinking process, reasoning, or meta-commentary.
