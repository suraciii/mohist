# Generic Integrate Stage Exploration

## Product Decision

Integrate should become a normal user-definable workflow stage.

Mohist should not assume that every issue must end by merging locally into the base branch. A project may want:

- local merge into the base branch
- GitHub PR creation and handoff
- spec sync and archive only
- artifact/report generation only
- no merge at all, with completion meaning "workflow evidence is complete"
- deployment, publish, or release handoff in a later custom stage

Therefore `Stage.Integrate` must not carry hidden system meaning. It should be just another stage id in the workflow definition. Delivery behavior must come from explicit tasks/checks and their `uses` contracts.

Target model:

```text
Stage = ordered user decision boundary
Task = executable work that may mutate state
Check = read-only verification
Use contract = behavior, side effects, outputs, recovery semantics
Workflow completion = terminal stage completion, not stage name
Delivery evidence = outputs from delivery-producing uses, not Integrate itself
```

## Current Problem

The current implementation still treats Integrate as a special final delivery stage:

```text
workflow passed
  requires final stage == integrate
  requires current stage == integrate
  requires integrate:spec-sync completed
  requires integrate:archive-change completed
  requires integrate:merge completed
  requires health:integrate passed
  requires merge landedSha
```

This conflicts with custom workflow v1.

A workflow like this should be valid:

```yaml
workflow:
  id: project/no-local-merge
  stages:
    - id: plan
      tasks:
        - id: design
          uses: mohist/agent
          with:
            prompt: Write design.md
      checks:
        - id: design-file
          uses: mohist/artifact-exists
          with:
            path: design.md

    - id: build
      tasks:
        - id: implement
          uses: mohist/agent
          with:
            prompt: Implement the design
      checks:
        - id: tests
          uses: mohist/shell
          with:
            command: npm test
```

If all work completes, the issue should be completed. It should not become blocked only because there is no Integrate stage or no local merge.

Another valid workflow:

```yaml
workflow:
  id: project/pr-delivery
  stages:
    - id: build
      tasks:
        - id: implement
          uses: mohist/agent
          with:
            prompt: Implement issue
      checks:
        - id: tests
          uses: mohist/shell
          with:
            command: npm test

    - id: integrate
      tasks:
        - id: open-pr
          uses: mohist/github-pr
          with:
            base: main
      checks:
        - id: pr-ready
          uses: mohist/pr-ready
```

Here `integrate` is still a useful stage name, but its meaning is defined by `open-pr` and `pr-ready`, not by hardcoded local merge rules.

## Required Semantic Shift

### 1. Workflow Completion

Completion must be definition-driven.

Instead of:

```text
passed run is valid only when final stage is integrate
```

Use:

```text
passed run is valid when the terminal stage in this workflow is passed
```

The simplest v1 rule can be:

```text
terminal stage = last stage in stageOrder
completion = all terminal-stage tasks completed, all checks passed, approval approved if required
```

Later workflow definitions may add explicit terminal metadata:

```yaml
workflow:
  terminal: done
```

or:

```yaml
stages:
  - id: publish
    terminal: true
```

But v1 can use "last stage is terminal" safely.

### 2. Delivery Evidence

Delivery evidence must be produced by tasks/checks, not by a stage name.

Examples:

```text
mohist/merge completed
  -> delivery.merge.landedSha
  -> codeLocked = true

mohist/github-pr completed
  -> delivery.pr.url
  -> mergeState = pr-open

mohist/openspec-sync completed
  -> delivery.specSync

mohist/archive-change completed
  -> delivery.archive
```

The UI should scan workflow run outputs for delivery-producing uses, then show a delivery summary:

```text
Delivery
  PR opened: https://github.com/org/repo/pull/123
  Checks: passed
```

or:

```text
Delivery
  Local merge: landed abc123
  Specs synced
  Change archived
```

or:

```text
Delivery
  No delivery tasks declared
  Workflow completed after build evidence
```

### 3. Code Locking

Code locking must come from side-effect contracts.

Instead of:

```text
stage == integrate means code is locked
```

Use:

```text
mohist/merge completed means code is locked
mohist/github-pr merged means code is locked
```

If a workflow has no merge-producing use, Mohist should not pretend that code was merged. It can still complete the issue if the workflow definition says the workflow ends there.

### 4. Retry and Recovery

The hard part is side effects.

Generic stages can contain tasks with very different retry semantics:

```text
mohist/agent          usually retryable, may mutate worktree
mohist/shell task     depends on command
mohist/openspec-sync  should be idempotent or checkpointed
mohist/archive-change should be idempotent or checkpointed
mohist/merge          irreversible after landed commit
mohist/github-pr      idempotent if PR already exists
```

This means recovery belongs on `uses` contracts:

```text
UseContract
  mutates: boolean
  sideEffect: none | worktree | spec-state | branch | remote-pr | merge
  idempotency: idempotent | checkpointed | irreversible
  retryPolicy: allowed | blocked-after-success | requires-user
  deliveryEvidence?: schema
  locksCode?: boolean
```

Generic Integrate is safe only if each mutating use owns its recovery contract.

### 5. UI

The UI should stop special-casing Integrate as the only delivery panel source.

Current mental model:

```text
Integrate panel = delivery evidence
```

Target:

```text
Workflow panel = tasks/checks by stage
Delivery summary = delivery-producing task/check outputs across the run
Merge truth banner = merge/pr/local delivery state, if any
```

If a workflow has no merge task, the merge banner should say that clearly:

```text
No local merge configured
Workflow completed without merge
```

If a workflow opens a PR:

```text
PR opened
Waiting for GitHub merge
```

This is more honest than forcing every workflow through local `integrate:merge`.

## Domain Model Direction

```text
WorkflowDefinition
  id
  stages[]
  completionPolicy?

StageDefinition
  stage
  title?
  tasks[]
  checks[]
  requiresApproval?
  terminal?

TaskDefinition
  id
  title
  uses
  with
  source

CheckDefinition
  name
  title
  uses
  with
  source

UseDefinition
  name
  placement
  mutates
  sideEffect
  idempotency
  deliveryEvidence?
  locksCode?

WorkflowRun
  workflowDefinitionSnapshot
  stageRuns[]
  deliverySummary
```

The key invariant:

```text
No stage name carries hidden delivery behavior.
Only workflow definition and use contracts define behavior.
```

## Implementation Implications

### Remove Integrate-specific Completion Projection

`WorkflowRunProjection.validateCompletionProjection()` should stop requiring:

```text
final stage == integrate
integrate delivery evidence exists
```

It should validate:

```text
snapshot.status == passed
currentStage == last stage in stageOrder
last stage status == passed
all completed tasks satisfy their uses contract
```

If a workflow declares no delivery-producing uses, completion is still valid.
If a workflow declares `mohist/merge`, that task cannot be marked completed
unless the merge use contract has valid landed-commit evidence.

### Generalize Stage Completion Guards

Domain `WorkflowRun.evaluateStageCompletionGuard()` currently has special guards for Check and Integrate. These should become definition-driven policies.

Short-term:

- keep default workflow behavior by making default delivery work explicit
  tasks/checks
- use task `uses` contracts to decide whether a completed task has enough
  output evidence
- use checks for any read-only verification, including cross-task or external
  state verification

Long-term:

```yaml
checks:
  - id: review-passed
    uses: mohist/verdict
  - id: merge-landed
    uses: mohist/merge-landed
```

No third category is needed between task and check.

### Move Delivery Metadata Extraction to Use Outputs

Today delivery metadata is derived from specific task ids:

```text
integrate:spec-sync
integrate:archive-change
integrate:merge
health:integrate
```

Target extraction:

```text
for each completed task/check:
  use = origin.uses
  if use.deliveryEvidence:
    append to delivery summary
```

Default workflow can still use those ids, but the extraction should not require them.

### Add PR Delivery Uses

To support GitHub-first workflows, add future uses:

```text
mohist/github-pr
  task
  creates or reuses a PR
  deliveryEvidence: prUrl, branch, base, head
  idempotency: idempotent by branch/issue

mohist/pr-ready
  check
  verifies PR exists and is mergeable

mohist/pr-merged
  check
  verifies PR was merged
```

This allows workflows that do not merge locally.

## Recommended Next Issue

Create a follow-up issue:

```text
feat(workflow): make Integrate a generic stage and move delivery semantics to use contracts
```

Acceptance criteria:

- A full custom workflow can complete without an Integrate stage.
- A full custom workflow can include an `integrate` stage without local merge.
- Completion projection uses the workflow's terminal stage, not `Stage.Integrate`.
- Default `mohist/default` still requires OpenSpec sync, archive, merge, and post-merge health through explicit task/check definitions.
- Delivery metadata is derived from delivery-producing uses, not hardcoded stage/task ids.
- Merge/code-lock state is produced by `mohist/merge`, not by the Integrate stage name.
- UI shows "completed without merge" or PR/local merge state honestly.
- Tests cover:
  - custom workflow `plan -> build` completes successfully
  - custom workflow `build -> integrate(open-pr)` completes without local merge
  - default workflow still enforces local merge delivery evidence
  - failed/partial delivery-producing task prevents completion only when that task is part of the workflow

## Non-Goals

- Do not add GitHub Actions style matrix builds.
- Do not add arbitrary marketplace actions.
- Do not make every task safely retryable by default.
- Do not hide merge/PR state behind generic "completed"; delivery truth must remain visible.

## Improvement Plan

The improvement should be staged around product semantics, not around deleting
`Stage.Integrate` references mechanically. The goal is to keep the current
default workflow just as strict while allowing custom workflows to define a
different ending.

### Step 1: Introduce Explicit Use Semantics

Extend builtin `uses` definitions so the engine can reason about behavior
without inspecting stage names or task ids:

```text
UseDefinition
  name
  placement
  mutates
  sideEffect
  idempotency
  deliveryRole?
  locksCode?
  evidence?
```

Initial delivery roles:

```text
none              normal work/check evidence
spec-sync         durable spec update evidence
archive           durable archive evidence
local-merge        landed local merge evidence
remote-pr          opened PR evidence
remote-merge       merged PR evidence
```

Default mappings:

```text
mohist/openspec-sync   deliveryRole=spec-sync
mohist/archive-change  deliveryRole=archive
mohist/merge           deliveryRole=local-merge locksCode=true
mohist/merge-ready     deliveryRole=none
mohist/agent           deliveryRole=none
```

This creates one vocabulary for completion, retry, delivery summary, and UI.

### Step 2: Make Completion Definition-Driven

The workflow run should complete when the terminal stage passes.

V1 rule:

```text
terminal stage = last stage in stageOrder
workflow passed = terminal stage passed
issue completed = workflow passed and completion projection accepts snapshot
```

Projection should validate structural truth and task contract truth:

```text
currentStage == terminalStage
terminalStage.status == passed
all terminal tasks/checks/approval are terminal according to the run snapshot
every completed task with a uses evidence contract has valid output
```

Projection should not require:

```text
terminalStage == integrate
integrate:merge exists
landedSha exists when no local merge use is declared
```

### Step 3: Preserve Default Strictness Through Tasks, Checks, and Uses

The built-in workflow should still require:

```text
integrate:spec-sync
integrate:archive-change
integrate:merge
health:integrate
```

But this should be expressed as normal tasks/checks:

```text
integrate:merge is a task using mohist/merge.
mohist/merge says completed output must include landedSha.
Therefore default workflow cannot complete without landedSha.

health:integrate is a check using mohist/health-gate.
The stage cannot pass unless that check passes.
```

For a custom workflow:

```text
Custom workflow declares no delivery-producing uses.
Therefore it may complete without merge evidence after its tasks/checks pass.
```

For a PR workflow:

```text
Custom workflow declares remote-pr delivery.
Therefore it may complete with prUrl instead of landedSha.
```

### Step 4: Split Stage Completion Guards

Current special guards should be decomposed:

```text
generic stage guard
  all ordered tasks terminal
  all checks passed/skipped according to policy
  approval approved if required

task contract guard
  each completed task satisfies its uses evidence contract

check guard
  checks are read-only verification and must pass according to policy

default workflow compatibility
  exists only as explicit default tasks/checks/uses, not as stage-name logic
```

This keeps Check and Integrate from being permanently special. A stage named
`release`, `publish`, `handoff`, or `integrate` should all use the same engine
rules.

### Step 5: Move Delivery Summary Out of Integrate

API projection should build delivery metadata by scanning task/check origins:

```text
for each stageRun:
  for each task/check:
    use = origin.uses
    if use.deliveryRole != none:
      collect normalized delivery evidence
```

The UI then renders:

```text
Delivery summary
  Local merge landed: abc123
  Specs synced
  Change archived
```

or:

```text
Delivery summary
  PR opened: https://github.com/org/repo/pull/123
```

or:

```text
Delivery summary
  No delivery task configured
  Workflow completed after declared checks passed
```

This is important because "completed" and "merged" are not the same product
state.

### Step 6: Keep Merge Bypass and Retry Rules Use-Based

Existing direct merge protection should also stop saying "only Integrate can
merge". The product rule should become:

```text
Direct merge is allowed only when the current workflow has a pending
delivery-producing local merge task or an explicit manual merge action.
```

Retry rules should depend on side effect idempotency:

```text
idempotent          retry automatically or safely reuse result
checkpointed        resume from recorded result
irreversible        block retry after success; require user intervention
```

This prevents a generic stage from accidentally rerunning a landed merge or
creating duplicate PRs.

## Suggested Implementation Slices

### Slice A: Semantics Catalog

- Extend `WorkflowUseDefinition` with `sideEffect`, `idempotency`,
  `deliveryRole`, `locksCode`, and optional evidence fields.
- Keep existing `uses` names and default behavior.
- Add tests for the builtin catalog.

This slice should not change runtime behavior yet.

### Slice B: Terminal Completion

- Replace Integrate-only completion projection with terminal-stage projection.
- Keep default workflow strict through task uses evidence contracts and checks.
- Add tests for:
  - custom workflow without Integrate completes
  - custom workflow with Integrate but without merge completes
  - default workflow still rejects missing merge delivery

### Slice C: Generic Delivery Summary

- Replace Integrate-only `deliveryMetadata()` with use-origin based delivery
  extraction.
- Preserve current local merge fields for the default workflow.
- Add neutral rendering for "no delivery configured".

### Slice D: Recovery and Locking

- Replace `stage == integrate && task == integrate:merge` freeze logic with
  `use.locksCode` or `deliveryRole=local-merge`.
- Ensure retry/rerun refuses to repeat irreversible successful side effects.
- Keep opencode ACP task session creation/reuse unchanged for `mohist/agent`.

### Slice E: Future PR Delivery

- Add definition-level shape for `mohist/github-pr`, `mohist/pr-ready`, and
  `mohist/pr-merged`.
- Runtime implementation can come later; the domain model should not require
  local merge once these uses exist.
