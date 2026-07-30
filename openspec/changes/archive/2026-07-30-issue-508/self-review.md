# Self-Review — Issue 508 (round 2)

Reviewer role: critique the plan artifacts against the issue and each other.
Only this file was modified. This round re-checks the artifacts after the round-1
findings (F1–F3) were addressed.

## What was checked

- Round-1 findings (F1 enable-toggle home, F2 T-005 dependency, F3 Resolver suffix).
- Complete public-method inventory of the three managers → every method has a task home.
- Cross-class consumers of any manager member that a deletion task would break.
- Spec format across all four capabilities.
- Task graph (DAG, priorities, dependencies).
- Internal consistency: proposal ↔ design ↔ tasks ↔ specs.

## Round-1 findings — resolved

- **F1 (enable-toggle home) — RESOLVED.** The Profile enable toggle now has an
  explicit destination across all artifacts: design D2 (provider owns "collection
  **and enablement**"), D5 (migration of `GetDisabledWorkflowProfileIdsAsync` +
  `SetProfileEnabledAsync` onto `IWorkflowProfileProvider`, six consumers rewired),
  proposal (What Changes + Impact name `IWorkflowProfileProvider`), the
  `workflow-profile-resolution` spec (new "Profile enablement is owned by the
  Profile provider" requirement, 3 scenarios), and T-006 (folds in the move).
  `ProjectWorkflowProfileManager` is explicitly deleted once empty.
- **F2 (T-005 dependency) — RESOLVED.** T-005 `dependsOn` is now `[T-003]` (the
  real shared-file coupling: both edit `MohistIssueWorkflowProfileBase`); the note
  explains it runs after T-004 by priority but shares no files with it.
- **F3 (Resolver suffix) — RESOLVED.** Design D3 is now a firm decision (all three
  run-context units resolve run context to one canonical effective resource →
  `Resolver`); the "to be confirmed at implementation" deferral and the matching
  Open Question entry are gone.

## Method-inventory audit (no stranded methods)

Every public member of the three managers maps to a task:

- `WorkflowProfileManager` — Definition methods (LoadTemplate/StageSpecs/Structure/
  StartupStructure/ApprovalConfig → T-004), variable merge
  (ResolveEffectiveVariables/LoadIssueWorkspace + internals → T-002), prompts
  (LoadPrompt(s)/RenderPrompt → T-003). All covered.
- `ProjectWorkflowProfileManager` — variables → T-001; prompts (incl. the dead
  override path + `ListSystemPromptsAsync`) → T-003; enable toggle → T-006;
  template CRUD + default + system-catalog helpers → T-006. All covered.
- `IssueWorkflowProfileManager` — variables → T-001; template-selection write
  (`UpdateTemplateAsync`/`GetStateAsync`/`GetTemplateAsync`, all already uncalled
  by any source) → T-006 dead-code removal. All covered.

Cross-class break check: the only call to a `ProjectWorkflowProfileManager` member
from outside its own file is `WorkflowProfileManager.cs:105` →
`GetSystemTemplateInfo(...)`. That call site is the Definition resolver, which
T-006 switches onto `IWorkflowProfileProvider`; once switched, the static helper is
unreferenced and is deleted in the same task. No deletion leaves a dangling
reference. The `GetTemplateAsync` mention in `IssueWorkflowProfileManager.cs:46`
is a comment, not a call.

## Verified sound

- **Spec format.** 4 capabilities, 27 requirements, 48 scenarios; every scenario
  uses exactly 4 hashtags (`#### Scenario:`) with WHEN/THEN; no delta headers.
- **Task graph.** Valid JSON, acyclic DAG, all `dependsOn` reference strictly
  lower-priority tasks, priorities contiguous 1–6.
- **No behavior-change drift.** The enable-toggle move and legacy retirement
  preserve external behavior (same data, same consumers, relocated host); the
  inline-YAML write path removal is dead-code removal (no source caller). The
  proposal's "behavior preserved" contract holds.
- **Done-When coverage.** All eight issue Done-When items map to a spec and/or task.

## Minor observations (non-blocking)

- **T-006 is the largest task** (enable-toggle migration + legacy CRUD retirement +
  IssueGrain default switch + dead inline-YAML removal + class deletion). It is
  coherent as "consolidate all Profile authority onto the provider," but an
  implementer may wish to split the enable-toggle rewire from the legacy deletion
  into two commits within the task. The design D7 already sequences these (switch
  readers first, then delete), so this is a commit-granularity note, not a plan gap.
- The static system-catalog helpers (`GetSystemTemplateInfo`/`GetSystemTemplateDefinition`)
  are consumed by the resolver and the enable-toggle write; both consumers move
  within T-006, so deletion is safe — noted here only so the implementer switches
  the resolver's `ProjectWorkflowProfileManager.GetSystemTemplateInfo` call to a
  provider/catalog call before deleting the helper.

## Verdict

All round-1 findings are resolved, the method inventory is fully covered with no
stranded members or dangling cross-class references, and the artifacts are
mutually consistent. The plan is ready to build.

<promise>PASS</promise>
