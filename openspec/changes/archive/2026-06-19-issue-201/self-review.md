# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: completeness
  Evidence: Spec requirement "Default template is non-deletable but disableable" had its *disable* half covered by task ACs (T-001 disable filter, T-002 disabled-default excluded from list), but no AC explicitly asserted the *non-deletable* guarantee ("WHEN an operation attempts to delete mohist/default THEN the operation SHALL be refused"). Added an AC to T-001 stating the built-in `mohist/default` is non-deletable (no delete operation exposed in registry/API/CLI).
  Verification: `tasks.json` re-parsed as valid JSON; T-001 now has 8 ACs incl. the non-deletable criterion; DAG/priority invariants re-validated.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-001 bundles the domain model, in-binary default, registry, DB row, and EF migration into one task. It is large, but cohesive — it is a single functional slice (the server-side issue-template catalog + persistence) and matches design Decision 1. It is NOT over-fine (no "define interface / register DI / standalone test" sub-tasks).
  SuggestedAction: During implementation, if T-001 grows beyond one PR's worth, the only safe split seam is "in-binary default + registry (built-ins only)" vs "project customs + disable persistence" — but only if needed; default to keeping it as one slice.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: alignment
  Evidence: The `defaults` field (labels/risk/workflow) is modeled and returned by the API, but MVP only prefills the *body skeleton* in the Web UI; applying `defaults` to the create-issue form fields is intentionally deferred (design Open Question, T-004 note). This is consistent with the issue AC, which scopes the Web UI to "select → prefill body skeleton".
  SuggestedAction: Schedule a fast-follow issue for applying `defaults` to the create-issue form on template select, once MVP lands.
  Status: follow-up

## Traceability Summary

| Issue Acceptance Criterion | Spec Requirement | Task |
|---|---|---|
| template schema (frontmatter + sections) | Issue template schema | T-001 |
| built-in mohist/default, 5 sections + guidance from skill | Built-in default template mohist/default | T-001 |
| CLI `mo issue template list` / `get <name>` | Template list and get CLI | T-003 |
| API list / get endpoints | Template list and get API | T-002 |
| Web UI selector → prefill body skeleton | Web UI template selector prefills body skeleton | T-004 |
| project can add custom template (data entry) | Project can add custom templates at the data layer; Issue template is a project-scoped resource | T-001 |
| suitable_for shared matching semantics with workflow profile | Template suitable_for and isDefault mirror workflow profiles | T-001 (+ T-002 surface) |

All 7 issue ACs, all 10 spec requirements, and all 4 tasks are mutually traced. Naming is consistent (`issue-template` capability, `IssueTemplate*` domain, `/api/issue-templates`, `ProjectIssueTemplateRow`, `mo issue template`). Dependency graph is a valid DAG with every `dependsOn` pointing to a strictly-lower-priority task. No standalone TEST tasks; tests are folded into each task's AC.

<promise>PASS</promise>
