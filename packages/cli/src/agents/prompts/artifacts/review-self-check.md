# Review Self-Check

Verify the review report you just generated is properly formatted and complete.

## What to Verify

Read `{changeDir}/review.md` and check:

### Format
- Starts with `# Review Report` heading
- Has a `## Verdict: PASS` or `## Verdict: FAIL` section
- Has `## Dimensions` section with sub-sections for Correctness, Complexity, Test Coverage, and Security
- Each dimension has a PASS/FAIL verdict
- If any dimension FAILS, the overall verdict is FAIL

### Completeness
- All changed files are covered in the findings
- Each dimension has concrete findings (not empty)
- Fix suggestions reference specific file paths and line numbers
- No placeholder text like `[findings]` remains

### Content Quality
- Findings are specific and actionable
- No thinking/reasoning process is present in the report
- No meta-commentary (e.g. "I reviewed the code..." or "Now I will check...")
- The report is purely the structured review output

## Actions

If the report fails any check above, rewrite `{changeDir}/review.md` with a corrected version.

If the report passes all checks, do nothing — leave the file as-is.

## Output

Output ONLY the final review report content. Do NOT include any thinking process, reasoning, or meta-commentary in your response.
