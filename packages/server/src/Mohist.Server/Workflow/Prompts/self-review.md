## Review Criteria

### Alignment
- Proposal addresses the actual issue — every "What Changes" entry traces back to an issue requirement
- No requirements from the issue are missing or misinterpreted

### Completeness
- All requirements covered by specs
- All specs have tasks
- Edge cases considered

### Consistency
- Specs align with proposal Capabilities
- Tasks reference correct spec files
- Design aligns with specs
- Naming consistent

### Feasibility
- Dependencies available or created by earlier tasks
- No circular dependencies
- Task granularity appropriate

### Dependency Completeness
- Every non-first task has `dependsOn`
- All `dependsOn` point to existing IDs with lower priority
- No cycles

## Actions

If issues found:
1. Fix directly in affected files
2. Ensure fixes don't break other artifacts
3. Do NOT delete artifacts

If all pass, do nothing.

## Output Format

Produce self-review.md with the following structured format. Every reported item MUST include `ID`, `Severity`, `Evidence`, and `Status`. Include `SuggestedAction` for all blocking items. Include `Scope` where applicable.

```
# Self Review Report

## Result: PASS / FAIL

## Repaired Items
(Items you fixed directly. Omit section if none.)

- [ID: item-N]
  Severity: info
  Scope: alignment | completeness | consistency | feasibility | dependencies
  Evidence: What was wrong and what was changed
  Verification: How the fix was verified
  Status: resolved

## Blocking Items
(Items that prevent PASS and were not repaired. Omit section if none.)

- [ID: item-N]
  Severity: blocking
  Scope: alignment | completeness | consistency | feasibility | dependencies
  Evidence: What is wrong
  SuggestedAction: What should be done
  Status: open | unresolved

## Follow-up Items
(Non-blocking suggestions for improvement. Omit section if none.)

- [ID: item-N]
  Severity: follow-up
  Scope: alignment | completeness | consistency | feasibility | dependencies
  Evidence: Description
  SuggestedAction: Suggested improvement
  Status: follow-up
```

## Verdict Marker

You MUST include exactly one of these tags on its own line at the end of self-review.md:

- If all checks pass: `<promise>PASS</promise>`
- If any check fails: `<promise>FAIL</promise>`

Do NOT include more than one marker. Do NOT include markers in code examples, explanations, or quoted text. The marker must be the final machine-readable verdict.
