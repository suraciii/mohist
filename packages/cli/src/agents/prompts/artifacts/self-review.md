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
