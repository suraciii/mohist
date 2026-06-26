# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `design.md` Open Question 1 ("Does the server's PATCH /variables already remove keys on null?") was left as an open question, but it was definitively resolved during task generation: `VariableBundle.DeepMerge`/`MergeNode` (`packages/server/src/Mohist.Server/Workflow/Domain/VariableBundle.cs:163,174`) `continue` past null-valued overlay properties, so `{ "foo": null }` is a no-op and the key persists. The design was therefore inconsistent with the already-decided plan. Changed the entry to mark it RESOLVED with the finding and a pointer to task T-001 that makes the spec assumption true.
  Verification: Re-read `VariableBundle.cs` lines 147-189 confirming the `JsonValueKind.Null` → `continue` / `return existing` branches; the resolved note now matches T-001's scope and the `issue-workflow-profile` spec's null-clear requirement.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: The `spec` field of T-002 references only the "CLI provides mo issue workflow config command group" requirement, but T-002 also delivers the "CLI issue workflow config get reads the workflow profile" and "CLI issue workflow config preview renders a prompt" requirements (covered explicitly in its description and acceptance criteria). The single-string `spec` field is narrower than the task's actual spec coverage.
  SuggestedAction: Optionally broaden T-002's `spec` reference (or add a multi-requirement list) for tighter traceability. Not required — every spec requirement is already implemented by exactly one task and all acceptance criteria are covered; this is purely a reference-precision nicety.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: alignment
  Evidence: The issue's API table lists an optional `PUT /variables` replace-semantics path ("覆盖语义，按需选"). The plan deliberately defers it (design Open Question 2; v1 uses PATCH merge only). This is a documented scope decision, not a gap, but the replace mode is not covered by any spec requirement.
  SuggestedAction: If replace-semantics becomes needed, add a `--replace-vars` flag and a corresponding spec requirement in a follow-up issue. No action for v1.
  Status: follow-up

## Review Notes

Cross-checked every artifact against the issue:

- **Alignment**: All 10 acceptance criteria are covered — `get` (T-002), `set --template/--var/--stage-var/--prompt` (T-003), `clear --template/--var/--prompt` (T-004), `preview` (T-002), shared `--project/--project-id` + `-o table|json` (T-002..T-004), `--help` lists all four verbs (T-002), CLI integration tests incl. error passthrough (T-002..T-004). All Non-Goals respected. The issue's "pure CLI wiring, server untouched except possible variable-clear tweak" is honored: the only server change is T-001, which the issue explicitly permits.
- **Completeness**: cli-interface spec (6 requirements) and issue-workflow-profile spec (1 requirement) are each implemented by a task. No requirement is orphaned. Edge cases covered: malformed `k=v`/`stage.k=v`, `@file` vs inline bodies, no-op guards, prompt-key escaping, server-error passthrough, null-clear on absent key (no-op) vs present key (removal).
- **Consistency**: Both spec capabilities match the proposal's "Modified Capabilities" (cli-interface, issue-workflow-profile); spec folder names match existing `openspec/specs/` entries; both spec files correctly use `## ADDED Requirements` (new concerns, not edits to existing requirement text). Task `spec` paths all resolve to existing files/requirements.
- **Feasibility**: No over-fine tasks — no "define interface", "register DI", file-move, or standalone test tasks; each task is a complete feature slice with tests bundled inline. Server change (T-001) is cleanly separated from CLI wiring (T-002..T-004).
- **Dependency completeness**: T-001 (p1, no deps), T-002 (p2, no deps — read path needs no server change), T-003 (p3, → T-002 group skeleton), T-004 (p4, → T-002 + T-001, since `--var` removal only works after the null-clear tweak). All `dependsOn` point to existing IDs with strictly lower priority; graph is a DAG (verified programmatically).

<promise>PASS</promise>
