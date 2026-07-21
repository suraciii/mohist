## Findings

### High: Historical duplicate selection is contradictory and lacks a normative contract

The issue's Fix Shape says to fall back to the most recent same-name session, but its Non-Goals explicitly defers deciding which record to use when multiple same-name sessions exist. The plan does not surface that contradiction for resolution. Instead, `design.md` lines 35 and 41 and `tasks.json` lines 9 and 12 require `CreatedDescending` with one result, while `design.md` lines 20, 55, and 67 narrow the deferred decision to identical creation-time ties without support from the issue.

The capability spec never states that the newest session must win and has no multiple-match scenario, so the design and task acceptance criteria impose behavior outside the normative contract. Before build, the artifacts must choose one consistent scope: either define and test newest-by-creation-time in the spec and clarify that only equal-time tie-breaking is deferred, or remove the ordering requirement from design/tasks and leave all multi-match selection to the backlog item.

## Review Summary

The remaining plan is coherent: it keeps historical fallback on metadata/transcript reads, preserves active-run precedence and runtime-session filtering, isolates project/issue/source labels, and avoids broadening issue-scoped commands. One implementation task is an appropriate vertical slice, and `tasks.json` is valid JSON with an acyclic dependency graph.

<promise>FAIL</promise>
