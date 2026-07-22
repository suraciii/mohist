# Self-Review: Issue #431 — Template Namespace Closure

## Summary

The plan closes the Workflow template language to ten roots, enforces fail-fast
rendering across all entry points, relocates approval feedback under
`work.approvalFeedback`, and migrates builtin profiles and prompts. The proposal,
specs, design, and tasks are largely consistent and comprehensive. All 12
acceptance criteria and 7 non-goals from the issue are addressed.

## Acceptance Criteria Trace

| AC | Coverage | Status |
|----|----------|--------|
| 1 — Builtin content no off-table roots | T-004, builtin-content-migration spec | Covered |
| 2 — vars-only resolution, no bare names | T-001 + T-002, template-namespace-closure spec | Covered |
| 3 — issue.projectId / --project | T-004, builtin-content-migration spec | Covered |
| 4 — work.approvalFeedback, no command | T-001, approval-feedback-context spec | Covered |
| 5 — unresolvable expression fails | T-002, template-evaluation-semantics spec | Covered |
| 6 — scalar-only interpolation, type, escape, nesting | T-002, template-evaluation-semantics spec | Covered |
| 7 — failure.* recovery-only, unchanged | T-002, template-evaluation-semantics spec | Covered |
| 8 — unified behavior vectors across entry points | T-002 + T-003, template-evaluation-semantics spec | Covered |
| 9 — inline agent parity | T-001 + T-002, template-evaluation-semantics spec | Covered |
| 10 — #465 invariants hold | T-002 notes + acceptance criteria (npm test) | Covered (see finding F-02) |
| 11 — end-to-end behavior preserved | T-004, builtin-content-migration spec | Covered |
| 12 — docs synced | T-004 acceptance criteria | Covered |

## Findings

### F-01 (SIGNIFICANT): Design omits `AuthoritativeRoutingOverlay` as a source of off-table roots

**Location:** design.md D1; tasks.json T-001.

The design identifies `IssueVariableBuilder.BuildBuiltInContext` as the sole
source of off-table roots baked into the `VariableBundle`. However, there is a
second source: `AuthoritativeRoutingOverlay.Apply`
(`packages/server/src/Mohist.Server/Workflow/Services/AuthoritativeRoutingOverlay.cs:84-112`),
called by `WorkflowProfileManager.ResolveEffectiveVariableBundleAsync`
(`WorkflowProfileManager.cs:246`) at dispatch time — the very method
`BuildPayloadAsync` calls to resolve effective variables.

The overlay injects `mohist` (line 37), `project` (line 45), `issue` (line 53),
`repository` (line 61), and `workspace` with `changeDir` (line 71-77) into the
effective variable bundle's `Vars`. These end up inside `vars` in the dispatch
payload. Even after cleaning `BuildBuiltInContext` and removing the hoisting
loop, `vars` would remain contaminated with these runtime facts, violating the
spec requirement "vars contains only merged Variables" and the T-001 acceptance
criterion of the same wording.

The overlay also has a legitimate security purpose (preventing configurable
variables from redirecting repository/workspace routing mid-run), so it cannot
simply be deleted — its routing-enforcement concern must be relocated to the
payload-root layer where `BuildPayloadAsync` builds `repository` and `workspace`
directly.

The same `ResolveEffectiveVariableBundleAsync` path feeds
`WorkflowQuerier` (`WorkflowQuerier.cs:131`, the effective-variables API) and
`ResolveBindVariablesAsync` (`WorkflowItemTranslator.cs:509-513`), so the
overlay's contamination reaches display and bind surfaces too.

**Impact:** An implementer following only the design's D1 would clean
`BuildBuiltInContext` and the hoisting loop, then discover at test time that
`vars` is still contaminated. The T-001 acceptance criterion ("vars contains
only merged Variables without runtime context, tasks, prompts, or failure
copies") would catch this, but the design should identify the overlay as a
source so the implementer addresses it proactively rather than discovering it
through a failing test.

**Recommendation:** D1 should name `AuthoritativeRoutingOverlay` alongside
`BuildBuiltInContext` and describe how the overlay's routing-security concern is
preserved when its runtime-fact injection is removed (e.g., the overlay enforces
routing at the payload-root layer, not inside `vars`). T-001's description
should mention the overlay.

### F-02 (MINOR): AC-10 (#465 invariant) lacks an explicit acceptance criterion

**Location:** tasks.json T-002.

The #465 regression test (`packages/runner/src/runtime/opencode-models.test.ts`,
the model-a → model-b retry scenario) exists and runs as part of
`npm test -w packages/runner`. T-002's acceptance criterion "npm run typecheck
-w packages/runner and npm test -w packages/runner pass" implicitly covers it,
and T-002's notes explicitly call out the invariant. This is adequate but an
explicit acceptance criterion (e.g., "the existing #465 model-a → model-b retry
regression test passes unchanged") would make the coverage more visible and
guard against the scenario being silently skipped.

## Verified OK

- **`--project` flag:** The CLI supports `--project` as canonical with
  `--project-id` as alias (`MohistCliCommands.cs:59-60,75`). The AC's `--project`
  is correct; the spec's hedge is harmless.
- **`runner.*` not consumed:** No builtin profile or prompt references
  `runner.*`. Safe to remove.
- **Non-goals:** All 7 issue non-goals are correctly reflected in the design's
  Non-Goals section and respected by the tasks.
- **Task DAG:** T-001 → {T-002 ∥ T-003} → T-004. Valid, acyclic, dependencies
  point to strictly lower priorities.
- **Spec completeness:** Every spec requirement (16 across 4 capabilities) is
  addressed by at least one design decision and at least one task.
- **Inline agent parity:** D6 + T-001 cover both `IsInlineAgentUses` sites
  (`WorkflowItemTranslator.cs` and `WorkflowYamlSerializer.cs`).
- **Failure dispatch root:** Design acknowledges the `failure` root completeness
  question (open question) and T-001 completes it to include `error`.

<promise>PASS</promise>
