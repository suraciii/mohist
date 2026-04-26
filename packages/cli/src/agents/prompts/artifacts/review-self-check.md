# Review Self-Check

Verify the review report you just generated is properly formatted and complete.

## What to Verify

Read `{changeDir}/review.md` and check:

### Format
- Starts with `# Review Report` heading
- Has a `## Verdict: PASS` or `## Verdict: FAIL` section
- Has `## Dimensions` section with sub-sections for Correctness, Complexity, Test Coverage, Security, and Spec Compliance
- Each dimension has a PASS/FAIL verdict
- If any dimension FAILS, the overall verdict is FAIL

### Completeness
- All changed files are covered in the findings
- Each dimension has concrete findings (not empty)
- Fix suggestions reference specific file paths and line numbers
- No placeholder text like `[findings]` remains

### Spec Compliance Coverage
- Report has a `### Spec Compliance` section under `## Dimensions`
- If the prompt context included a `## Tasks & Acceptance Criteria` section, each acceptance criterion from that section is explicitly addressed in the Spec Compliance findings
- Findings reference specific spec requirements or acceptance criteria (e.g. "AC: 'hex colors must match #0ea5e9' — PASS: line 42 uses #0ea5e9"), not generic statements like "code looks correct" or "implementation matches spec"
- Each AC finding states PASS or FAIL with concrete evidence (file path, line number, actual value)
- If no spec context was provided in the prompt, the Spec Compliance section should note that and be marked PASS

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
