Verify the review report is properly formatted and complete.

## What to Verify

Read `{changeDir}/review.md` and check:

- Starts with `# Review Report`
- Has `## Result: PASS` or `## Result: FAIL`
- Contains `<promise>PASS</promise>` or `<promise>FAIL</promise>` tag
- Has `## Dimensions` with Correctness, Complexity, Test Coverage, Security, Spec Compliance
- Each dimension has PASS/FAIL verdict
- If any dimension FAILS, overall verdict is FAIL
- All changed files covered
- Fix suggestions reference specific file:line
- No placeholder text like `[findings]` remains
- Spec Compliance explicitly addresses each acceptance criterion with concrete evidence
- No thinking/reasoning process present

## Actions

If the report fails any check, rewrite `review.md` with corrected version.
If it passes, do nothing.

Output ONLY the final review report content. No thinking process or meta-commentary.
