# Self-Review — Issue 508

Reviewer role: critique the plan artifacts against the issue and each other.
No artifact other than this file was modified.

## What was checked

- Internal consistency: proposal → specs → design → tasks.
- Coverage of the issue's Done When, Behavior Contract, and Non-Goals.
- Spec format (requirement/scenario headings, WHEN/THEN, normative language).
- Task graph (DAG, priority ordering, dependencies).
- Factual claims in the plan verified against the current code.

## Verified sound

- **Spec format is correct.** All 4 capabilities have a `spec.md`; every requirement
  has ≥1 `#### Scenario:` (exactly 4 hashtags); every scenario has WHEN/THEN; no
  `## ADDED/MODIFIED/REMOVED` delta headers; SHALL/MUST used throughout. (926 lines
  across 4 specs.)
- **Task graph is valid.** DAG, acyclic, every `dependsOn` points to a strictly
  lower-priority task; priorities contiguous 1–6.
- **Done When coverage is complete.** All 8 Done-When items map to a spec and/or task.
- **Dead-code claims hold up.** The 4 prompt-override/system methods D4 proposes to
  drop (`GetProjectPromptOverrideAsync`, `SetProjectPromptOverrideAsync`,
  `DeleteProjectPromptOverrideAsync`, `ListSystemPromptsAsync`) have **zero** callers
  repo-wide (src + tests). D4 is sound and in fact lower-risk than stated.
- **Arch-test claim holds.** `WorkflowProfileManager.cs` has exactly 3
  `WorkflowDefinitionResolutionException` throws (lines 71, 155, 356), all
  definition-resolution failures; the arch test requires ≥3 and they stay co-located
  under T-004's rename.
- **No hidden behavior change in T-006.** The Issue inline-YAML write path
  (`IssueWorkflowProfileManager.UpdateTemplateAsync`) has **no source caller** — it is
  already dead. Removing it (T-006) is dead-code removal, consistent with the
  proposal's "behavior preserved" contract. (The design D5 frames it as a live
  migration concern; it is actually already dead — minor framing nit, not a defect.)

## Findings

### F1 — BLOCKER: the Profile enable/disable toggle has no home in the decomposition

`ProjectWorkflowProfileManager` currently owns the system-Profile enable toggle:

- `GetDisabledWorkflowProfileIdsAsync` (`ProjectWorkflowProfileManager.cs:465`) — **6
  live consumers**: `IssueGrain.cs:319`, `IssueMetricsQuerier.cs`, `IssueQuerier.cs`
  (×3), `IssueReadModelLoader.cs`. It reads `ProjectWorkflowProfile.DisabledWorkflowProfileIds`.
- `SetProfileEnabledAsync` (`:474`) — the write side is dead (no caller), but the read
  side is live and feeds run-startup profile selection and read models.

The **proposal explicitly promises a destination** —
`proposal.md:67`: "decomposed into a Project Variables Store, a Prompts store/manager,
**and the enable-toggle home**".

But the downstream artifacts never deliver it:

- **Design D2 decomposition table** (`design.md:86–98`) has **no row** for the enable
  toggle. The only design mention (`design.md:12`) is in Context describing what the
  class *currently* holds — not a decision on where it goes.
- **No task** creates or rewires the enable toggle. T-001 takes variables, T-003 takes
  prompts, T-006 deletes template CRUD. After all six tasks, `GetDisabledWorkflowProfileIdsAsync`
  would be stranded in a `*WorkflowProfileManager` class that every other concern has
  left — directly violating the issue's "每个类只管一个资源" and "全仓 grep：「Profile」只指
  Workflow Profile 这一个资源" Done-When items.

This must be decided before building: assign the enable toggle a home (most naturally
Profile enablement is a Profile-membership concern → extend `IWorkflowProfileProvider`,
or a small Profile-config component), add it to the design D2 table, and either fold it
into an existing task or add a task that moves the read (and drops the dead write).

### F2 — Minor: T-005 dependency is sequencing, not output

`T-005` (IIssueWorkflowProfile split) declares `dependsOn: ["T-004"]`, but it does not
consume T-004's output. The genuine shared-file coupling is with T-003
(`MohistIssueWorkflowProfileBase`, which T-003 edits for prompt merge). Priority
ordering (T-003=3 < T-005=5) already sequences it correctly, so this is non-blocking;
noting for accuracy of the dependency rationale.

### F3 — Minor: design D3 leaves the two `Resolver` suffixes as an open question

D3 documents the `Resolver`-suffix tension for the variable-merge and prompt-resolution
units but defers confirmation to implementation. This is acceptable as an open question,
but since the issue's Done When requires "每个类名都能对上 conventions.md 的角色后缀表,"
the naming should be resolved in the design (it is a spec-level requirement), not left
to the implementer. Non-blocking but worth closing.

## Verdict

F1 is a must-fix: the plan promises an enable-toggle home that the design and tasks do
not deliver, leaving a live concern (6 consumers) stranded. The other findings are minor.

<promise>FAIL</promise>
