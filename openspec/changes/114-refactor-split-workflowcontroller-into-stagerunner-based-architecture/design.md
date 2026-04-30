## Context

`WorkflowController` (908 lines, not counting tests) mixes state machine orchestration, Plan/Build/Review stage logic, Git operations, ACP session lifecycle, and Checkpoint management in a single class. The `run()` method alone handles stage routing, approval gates, and error escalation while delegating to methods that each manage their own ACP connections, checkpoints, and side effects. This makes the class a high-risk change point: adding a new stage or modifying ACP timeout requires touching multiple unrelated code regions.

The existing `workflow/` directory contains only `index.ts`, `workflow-controller.ts`, and `workflow-loader.ts`.

## Goals / Non-Goals

**Goals:**
- Decompose `WorkflowController` into a `WorkflowEngine` (state machine loop only) + `StageRunner` interface + one runner class per stage
- Extract `Check` interface with `BuildTestCheck`, `MergeReadyCheck`, and `AiReviewCheck` implementations
- Create `AcpRoundRunner` to unify multi-round ACP session lifecycle across Plan and AiReviewCheck
- Create `CheckpointManager` to centralize checkpoint read/verify/upsert/delete logic
- Replace all `any` type dependencies with concrete interface types
- Produce 10 focused files of 40–150 lines each

**Non-Goals:**
- Changing any observable behavior or API surface
- Modifying the database schema
- Adding new workflow stages or checks
- Refactoring `workflow-loader.ts` or `workflow-controller.ts` tests

## Decisions

### D1: Replace `WorkflowController` with `WorkflowEngine` + `StageRunner` interface

The `run()` method's while/switch loop is the generic state machine. It selects a runner, calls it, handles success/failure/escalation/approval gates, then advances. This logic should not know about Plan rounds, Build tasks, or Check types.

Extracting it as `WorkflowEngine` lets each stage's business logic live in its own `StageRunner` implementation.

**Alternatives considered:**
- Extract an abstract `StageRunner` base class instead of an interface — would force inheritance constraints on runners that don't share a common base.
- Keep `run()` as a switch and add new case branches — violates open-closed principle; every new stage requires modifying the core file.

### D2: Introduce `Check` interface inside a `checks/` sub-directory

The `runPipelineReviewStage` method (908 lines) implements three sequential checks with very different implementation strategies: shell execution (`BuildTestCheck`), Git merge state inspection (`MergeReadyCheck`), and multi-round ACP with auto-fix (`AiReviewCheck`). These are structurally similar ("run something, return a result") but implementation-divergent.

A `Check` interface with three implementations allows `CheckStageRunner` to be a simple orchestrator that iterates over `Check[]`.

**Alternatives considered:**
- Keep checks as methods on `CheckStageRunner` — same file still grows with each new check type.
- Use a registry pattern with string keys — less type-safe than the interface approach.

### D3: Create `AcpRoundRunner` to consolidate ACP session lifecycle

Plan and AiReviewCheck each manage their own `createAcpConnection → ... → conn.close()` sequence with identical checkpoint-per-round patterns. Extracting `AcpRoundRunner.execute(issue, rounds, options)` eliminates the duplication and makes ACP timeout changes single-point.

The `RoundConfig` object (type, verify, label, outputPath, buildPrompt) is extracted from the inline `PlanRoundConfig` at the call sites.

**Alternatives considered:**
- Add ACP lifecycle to `CheckpointManager` — CheckpointManager should remain stateless and focused on persistence; ACP is a different concern.
- Use a shared utility function — insufficient; the method also captures scope-specific state (round index, event bus emissions).

### D4: Create `CheckpointManager` for all checkpoint operations

Currently Plan does:
```
get → parse completedSteps → verify → upsert → (on success) delete
```
Build does the same with its own `completedSteps` field names. Review does get/upsert without delete. These three implementations are nearly identical but live in different stage methods.

`CheckpointManager` exposes: `getResumeSteps(issueNumber, stage)`, `markStepComplete(issueNumber, stage, step, nextStep?)`, `delete(issueNumber, stage)`, `deleteAll(issueNumber)`.

**Alternatives considered:**
- Keep checkpoint logic in each stage — violates DRY and produces inconsistent behavior across stages.
- Extend `PipelineCheckpointRepo` — the repo is a thin persistence wrapper; business logic belongs in a manager.

### D5: Replace `any` types on `worktreeManager` and `projectRepo` with interface types

The controller uses `worktreeManager?: any` and `projectRepo?: any`. In the refactoring, we define minimal interfaces (`WorktreeManager`, `ProjectRepo`) with only the methods actually called. This enables static analysis of call signatures.

**Alternatives considered:**
- Use `unknown` and type guards — would require more boilerplate with no runtime benefit in this codebase.
- Import from the concrete classes — tight coupling; interface allows future substitution.

## File Layout

```
src/workflow/
├── index.ts                    ← updated exports: WorkflowEngine, StageRunner, Check
├── workflow-engine.ts          ← WorkflowEngine (~80 lines)
├── stage-context.ts            ← StageContext, StageRunResult, CheckContext, CheckResult types
├── plan-stage-runner.ts        ← PlanStageRunner (~150 lines)
├── build-stage-runner.ts       ← BuildStageRunner (~120 lines)
├── check-stage-runner.ts       ← CheckStageRunner (~60 lines)
├── checks/
│   ├── index.ts                ← Check interface, CheckResult
│   ├── build-test-check.ts     ← BuildTestCheck (~85 lines)
│   ├── merge-ready-check.ts    ← MergeReadyCheck (~59 lines)
│   └── ai-review-check.ts      ← AiReviewCheck (~120 lines)
├── acp-round-runner.ts         ← AcpRoundRunner (~120 lines)
├── checkpoint-manager.ts       ← CheckpointManager (~60 lines)
├── git-committer.ts            ← GitCommitter (~40 lines)
└── utils.ts                    ← readReportFile, cleanChangeDir, parseVerdict, etc.

src/workflow/workflow-controller.ts  ← DELETED
```

**Dependency direction:** `WorkflowEngine → StageRunners → AcpRoundRunner, CheckpointManager → PipelineCheckpointRepo`. No cycles.

## Risks / Trade-offs

- **[Risk] Refactoring without behavioral tests**: Without a test suite covering the current controller, we cannot automatically verify that moved code behaves identically. → **Mitigation**: Run existing tests after each extraction unit; if any fail, the extraction was too aggressive and must be reverted for that unit.
- **[Risk] `any` types hide logic errors**: The `worktreeManager` and `projectRepo` fields are `any` throughout the current class. Replacing them with interfaces may surface latent bugs. → **Mitigation**: Add interface methods only as they appear in existing call sites; do not introduce new behavior.
- **[Risk] Removing `buildReviewAcpOptions` dead code**: The issue description notes `buildReviewAcpOptions` and `runReviewRound` exist but are unused in the main review path. These may be called in edge cases discovered only at runtime. → **Mitigation**: Confirm via grep across the entire codebase that these are truly dead before deletion.

## Migration Plan

1. Read the entire `workflow-controller.ts` and document every method's side effects (EventBus emits, checkpoint operations, Git operations, ACP connections).
2. Define `StageRunner` interface and `StageContext` / `StageRunResult` types in `stage-context.ts`.
3. Define `Check` interface and `CheckResult` types in `checks/index.ts`.
4. Implement `CheckpointManager` — verify against existing checkpoint call sites in all three stages.
5. Implement `AcpRoundRunner` — verify against Plan and AiReviewCheck ACP usage patterns.
6. Implement each `Check` subclass by extracting code from `runPipelineReviewStage`.
7. Implement `PlanStageRunner`, `BuildStageRunner`, `CheckStageRunner` by extracting code from respective stage methods.
8. Implement `WorkflowEngine` by extracting the `run()` while/switch loop.
9. Wire up `workflow/index.ts` to export new classes instead of `WorkflowController`.
10. Update all importers of `WorkflowController` to use `WorkflowEngine` (should be only `server.ts` and test files).
11. Delete `workflow-controller.ts`.
12. Run full test suite; fix any failures.
13. Run `npm run build` and `npm run lint` to verify compilation and style.

**Rollback**: `git checkout workflow-controller.ts` restores the original. The refactoring is additive (new files) + subtractive (old file removed), so rollback is straightforward as long as no other files were modified.

## Open Questions

1. **Should `commitBuildChanges()` be part of `BuildStageRunner` or a separate `GitCommitter`?** The method is called once at the end of the build stage. Extracting it to a `GitCommitter` class keeps `BuildStageRunner` focused, but adds a new abstraction for a single-method class. Decision: extract to `GitCommitter` alongside `CheckpointManager` and `AcpRoundRunner` for symmetry.
2. **Where should the `parseVerdict` and `extractFixSuggestions` utility functions live?** These are used by `AiReviewCheck` only. Keep them in `utils.ts` under `workflow/` rather than in `ai-review-check.ts` to allow reuse if other checks need them.
3. **Should `PlanStageRunner` and `AiReviewCheck` share `AcpRoundRunner` or have separate instances?** Both need the same execution pattern. A single shared instance injected via constructor is simpler than two instances with the same behavior.
