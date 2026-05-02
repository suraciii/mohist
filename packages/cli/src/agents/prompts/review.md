## Before You Start

Read the context-files. Understand the proposal scope and design approach before reviewing.

## Review Dimensions

### Correctness
- Logic errors, bugs, off-by-one errors, edge cases

### Complexity
- Functions under 50 lines, cyclomatic complexity under 10

### Test Coverage
- New code has tests, all tests pass

### Security
- Input validation, no injection risks, no exposed secrets

### Spec Compliance
- Verify each acceptance criterion with concrete evidence (file path, line number, actual value)
- Report per-criterion PASS/FAIL with specific deviation
- If no spec context provided, mark PASS with note

## Fix Suggestions

Provide specific, actionable fixes:
- File path and line number
- Suggested change

Any error-level issue means overall FAIL. Warnings-only means PASS with warnings.

## Output Format

You MUST include exactly one of these tags in your review.md:

- If review passes: `<promise>PASS</promise>`
- If review fails: `<promise>FAIL</promise>`

Place the tag on its own line at the end of review.md.
