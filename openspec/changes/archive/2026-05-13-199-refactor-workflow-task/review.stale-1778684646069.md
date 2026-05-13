## Review: Issue #199 - refactor(workflow): unify task execution infrastructure

### Summary

The implementation introduces the intended shared workflow task runtime slice:

- `StageContext.emit` / `StageContext.log` shared side-effect helpers.
- Minimal task runtime contracts and registry.
- `StaticTaskLoader`.
- `AgentSessionTaskHandler`.
- `ServiceCallTaskHandler`.
- `RepairFixAdapter` for legacy repair/fix dispatch.

The blocking review finding from the previous review has been fixed. The code now preserves the existing workflow runner architecture while reducing duplicated emit/log and repair/fix dispatch paths.

### Blocking Findings

None.

### Fixed During Review

#### E1: Broken `emit` closure in `workflow-integration.test.ts`

`makeContext()` previously returned an object whose `emit` helper referenced the `ctx` variable declared inside the outer `describe` block. That made the fixture fragile and caused the `approval_requested` event assertion to miss the actual event path.

The fixture now captures the returned context object in a local `result` variable and forwards `emit` through `result.eventBus`, so the approval event path through `ctx.emit` is covered correctly.

### Validation

Passed:

```text
npm test -- --run tests/workflow/workflow-integration.test.ts
npm test -- --run tests/workflow/stage-context-emit-log.test.ts tests/workflow/task-runtime/*.test.ts
npm run build
```

Additional check:

```text
npm test -- --run tests/workflow
```

This broader workflow suite still has one pre-existing baseline failure in `tests/workflow/workflow-session-handling.test.ts`: the test expects a `session_failed` Ralph result status of `skipped`, while the current implementation returns `failed`. The same single-test failure reproduces on `master`, so it is not introduced by issue #199 and is outside this issue's task-runtime scope.

### Spec Compliance

| Acceptance Criterion | Status |
|---|---|
| Shared `emit` / `log` helpers replace runner-private helper duplication | PASS |
| Unified `TaskHandler` contract exists and returns `StageTaskResult` | PASS |
| Repair/fix paths can dispatch through shared adapter or preserved compatible wrappers | PASS |
| Legacy repair/fix exports remain available | PASS |
| `StaticTaskLoader` covers Plan/Check/Integrate static task expression in focused tests | PASS |
| `AgentSessionTaskHandler` and `ServiceCallTaskHandler` cover the minimum execution contracts | PASS |
| No single generic StageRunner cutover | PASS |
| No RalphExecutor split | PASS |
| No generic SSE event taxonomy | PASS |
| Focused Plan/Check/Integrate/repair-fix tests pass | PASS |

### Warnings / Follow-Up Candidates

- `BuildStageRunner` still uses the legacy health-fix wrapper directly. This is acceptable for this slice because the issue explicitly keeps legacy runner paths, but a later unification issue can route Build through the adapter as part of broader cutover.
- Adapter prompt builders are intentionally minimal compared with the existing legacy prompt builders. That is acceptable while legacy entrypoints remain available, but prompt parity should be considered before making the adapter the only path.
- `createRepairFixAdapter()` is currently created per invocation. This is low risk and can be optimized later if adapter construction becomes a measurable concern.

### Verdict

The blocking review issue has been fixed and the focused validation for issue #199 passes. The implementation satisfies the scoped task-runtime infrastructure slice without crossing into the explicitly deferred generic runner, Ralph split, or event taxonomy work.

<promise>PASS</promise>
