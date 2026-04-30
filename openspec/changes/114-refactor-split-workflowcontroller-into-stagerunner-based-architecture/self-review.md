# Self-Review Report

## Result: FAIL

## Completeness: PASS
- All major requirements from the issue are covered by tasks
- No spec files needed (this is pure code restructuring with no new capabilities)
- Missing `any` type replacement as a distinct task (see Quality section)

## Consistency: FAIL
- **Line count mismatch**: proposal.md says "1434 lines", design.md correctly says "908 lines". The `workflow-controller.ts` file is 908 lines. Proposal must be corrected to 908.
- **T-004 bundles three checks with different dependency profiles**: `BuildTestCheck` and `MergeReadyCheck` only need shell/git commands (no AcpRoundRunner), but T-004 forces them to wait for T-003 (AcpRoundRunner) because `AiReviewCheck` needs it. These two groups have fundamentally different dependency chains and should be separated.

## Feasibility: PASS
- All task dependencies flow in one direction (no cycles)
- Every non-first task has at least one `dependsOn`
- Each task has a clear, completable scope
- Implementation steps are actionable

## Dependency Completeness: FAIL
- **Missing task for `any` type replacement**: The issue explicitly calls out `worktreeManager?: any` and `projectRepo?: any` as critical problems. No task creates the `WorktreeManager` or `ProjectRepo` interfaces, nor replaces the `any` types in the new files. This is a core requirement of the refactoring that must be addressed.
- **T-004 dependency is suboptimal**: `BuildTestCheck` (shell exec) and `MergeReadyCheck` (Git state) do not need `AcpRoundRunner`. They should depend only on `T-001` (types). Only `AiReviewCheck` needs `AcpRoundRunner`. Splitting T-004 into two tasks (T-004a: BuildTestCheck + MergeReadyCheck; T-004b: AiReviewCheck) would allow the shell-based checks to proceed without waiting for T-003.
- **T-010 depends on nothing but should depend on T-001**: T-010 (utils.ts) creates utility functions that `stage-context.ts` types may reference indirectly. While not strictly required, the dependency would make the graph cleaner.

## Quality: FAIL
- **proposal.md line 3**: says "1434 lines" — must be corrected to "908 lines" to match the actual file.
- **proposal.md Capabilities section**: correctly states "no new capabilities" and "no modified capabilities", but this is inconsistent with the fact that `BuildTestCheck` and `MergeReadyCheck` do not currently exist in the 908-line `workflow-controller.ts` (they are mentioned in the issue description as part of the target architecture but not present in the current file). Creating them as part of T-004 is necessary but technically introduces new behavior. The proposal should clarify this.
- **Tests import non-existent functions**: `stage-auto-fix.test.ts` and `parse-dimensions.test.ts` import `parseVerdict` from `workflow-controller.ts`, and `review-auto-fix.test.ts` imports `parseResult` and `extractFixSuggestions` — none of which exist in the 908-line file. These functions must be implemented as part of the refactoring (likely in `utils.ts`) or the tests are broken. T-010's description mentions `parseVerdict` but doesn't explicitly create it.
- **No task for `any` type replacement**: Critical gap. The issue's #1 red flag is `any` types. Without a dedicated task, this may be done inconsistently or omitted.

## Fixes Applied

1. **proposal.md**: Changed "1434 lines" to "908 lines" in the Why section (line 3).
2. **tasks.json**: Split T-004 into T-004a (BuildTestCheck + MergeReadyCheck, depends on T-001 only) and T-004b (AiReviewCheck, depends on T-003 and T-004a). Renumbered subsequent tasks (T-005 through T-015). T-004a can run in parallel with T-003 since the shell-based checks don't need AcpRoundRunner.
3. **tasks.json**: Added T-015 (Replace `any` types with interface types) as a new task depending on T-001. This replaces `any` on `worktreeManager` and `projectRepo` with concrete `WorktreeManager` and `ProjectRepo` interfaces, applied across all runner files that use these dependencies.
4. **tasks.json T-010**: Updated description to explicitly mention creating `parseVerdict`, `extractFixSuggestions`, and `parseResult` functions that are imported by existing tests but do not exist in the current `workflow-controller.ts`.
5. **tasks.json T-004a notes**: Clarified that BuildTestCheck and MergeReadyCheck are new implementations not present in the current 908-line file — they must be created from scratch based on the issue's architectural description.
