## Before You Start

Read the context-files. Understand the proposal scope and design approach before reviewing.

## Comprehensive Review

You MUST perform a comprehensive review pass. Do NOT stop after finding the first blocker. Inspect ALL of the following before producing your final output:

1. Current issue acceptance criteria — verify each criterion with concrete evidence (file path, line number, actual value)
2. ALL changed files — review every file touched by the current change
3. Adjacent retry, recovery, and artifact paths — check for stale evidence, outdated references, or missing error handling
4. Regression coverage gaps — ensure new code has tests and all tests pass
5. Cross-cutting concerns — security, data safety, public contracts, migration impact

Only after completing this full pass should you produce your final structured output.

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

## In-Session Repair Policy

You MAY fix small, local, low-risk issues directly during this review session. Only repair items that are:

- Formatting, typos, missing obvious guards, small test expectation updates, import cleanup, dead code removal
- Changes that do not require product judgment, broad refactoring, cross-stage decisions, or unclear tradeoffs
- Fixes that can be verified by focused commands before you finish

You MUST NOT repair items that involve any of these reasons:

- Product behavior changes or public contract modifications
- Data safety risks, security posture changes, or merge strategy changes
- Architectural judgment, cross-file refactoring, or ambiguous solutions
- Changes requiring user or product-owner decisions
- Follow-up work outside the current issue scope

If you are unsure whether a fix is safe, report it as unresolved instead of fixing it.

## Recording Repairs

When you repair an item directly, you MUST record all of the following in the structured output:

1. Assign a stable ID to each finding (e.g., `item-1`, `item-2`). IDs must be unique across the entire report.
2. For repaired items, set `Status: resolved` and include:
   - `Scope:` describing what was changed (e.g., `formatting`, `typos`, `missing-obvious-guards`)
   - `Evidence:` describing what was wrong and what was changed
   - `Verification:` the command you ran to confirm the fix (e.g., `npm run build`, `npm test -- --grep "test name"`)
3. For unresolved items that should NOT be repaired in-session, set `Status: unresolved` or `Status: open` and include the disallowed reason in the evidence using the tag `[disallowed:reason]` (e.g., `[disallowed:product-behavior-change]`, `[disallowed:architectural-judgment-required]`)

## Verdict Rules

- Your final verdict MUST be based on the POST-REPAIR candidate snapshot
- If you repaired items, re-evaluate affected findings after your repair
- You MUST NOT produce PASS if any repaired item lacks verification evidence
- You MUST NOT produce PASS while unresolved blocking items remain
- Non-blocking follow-up or out-of-scope items do not prevent PASS
- Pre-existing failures discovered during review must be reported but do not block the current change

## Output Format

Produce a review.md with these sections. Every reported item MUST include all required fields: `ID`, `Severity`, `Evidence`, and `Status`. Include `SuggestedAction` and `Verification` for all blocking and unresolved items. Include `Scope` where applicable.

```
# Review Report

## Result: PASS / FAIL

## Repaired Items
(Items you fixed directly during this session. Omit section if none.)

- [ID: item-N]
  Severity: info
  Scope: formatting | typos | missing-obvious-guards | ...
  Evidence: What was wrong and what was changed
  Verification: Command run to verify the fix
  Status: resolved

## Blocking Items
(Items that prevent PASS for the current change and were not repaired.)

- [ID: item-N]
  Severity: blocking
  Scope: file path or area
  Evidence: What is wrong [disallowed:reason] (if repair was considered but disallowed)
  SuggestedAction: What should be done
  Verification: How to verify the fix
  Status: open | unresolved

## Follow-up Items
(Non-blocking suggestions for future improvement within the current issue scope.)

- [ID: item-N]
  Severity: follow-up
  Scope: file path or area
  Evidence: Description
  SuggestedAction: Suggested improvement
  Status: follow-up

## Pre-existing or Out-of-scope Items
(Pre-existing failures not introduced by this change, or items outside the current issue scope. These are visible for awareness but do not block the current workflow.)

- [ID: item-N]
  Severity: info | warning
  Scope: file path or area
  Evidence: Description of pre-existing or out-of-scope condition
  SuggestedAction: Optional suggested action for future consideration
  Status: pre-existing | out-of-scope
```

## Verdict Marker

You MUST include exactly one of these tags on its own line at the end of review.md:

- If review passes: `<promise>PASS</promise>`
- If review fails: `<promise>FAIL</promise>`

Do NOT include more than one marker. Do NOT include markers in code examples, explanations, or quoted text. The marker must be the final machine-readable verdict.
