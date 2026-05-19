## Findings

1. `fix-review-findings` cannot persist the structured reaction evidence that the convergence flow depends on, so the runtime never records authoritative attempted/resolved/unresolved item IDs from the actual repair task output.
File: `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:85-103`, `packages/cli/src/workflow/convergence.ts:67-104`, `packages/cli/src/workflow/check-stage-runner.ts:573-605`
Evidence: repair tasks dispatched through `createAgentSessionTaskHandler()` always return an `output` object containing only wrapper metadata (`kind`, `stage`, `attempt`, `success`, `error`, `acpSessionId`, `summary`). `extractReactionOutput()` only reads `attemptedItemIds`, `resolvedItemIds`, `unresolvedItemIds`, and `newItemIds` from that returned payload or its nested `result`; those fields are never populated by the handler. As a result, `persistReactionConvergence()` logs and saves nothing for real agent-session repair runs, violating the required reaction-output recording and verification-recheck evidence path in `REQ-WR-001`, `REQ-WE-001`, and acceptance criteria around full-batch reaction convergence.
Suggested fix: have the repair-task execution path read and return a declared structured result from the repair task’s durable output source, or let `fix-review-findings` explicitly write structured attempted/resolved/unresolved IDs into the `StageTaskResult.output` consumed by `extractReactionOutput()`.

2. Stage-state/API convergence projection reads the wrong JSON shape for both checks and tasks, so the advertised convergence fields disappear unless callers write denormalized top-level fields that the actual workflow checks do not produce.
File: `packages/cli/src/services/stage-state-service.ts:550-567`, `packages/cli/src/services/stage-state-service.ts:590-649`, `packages/cli/src/workflow/checks/review-passed-check.ts:76-82`
Evidence: `review-passed` stores structured data under `output.structuredResult`, but `extractStructuredResult()` only reads top-level `output.verdict`, `output.marker`, `output.items`, `output.repairedItemIds`, and `output.summary`. The same mismatch applies to task outputs. Therefore `computeConvergenceState()` misses blocking items, non-blocking items, and direct repairs for authoritative workflow outputs, and the API tests only pass because they insert synthetic top-level `items`/`repairedItemIds` rather than the runtime shape emitted by `review-passed`. This breaks `REQ-PM-STRUCTURED-001` and `REQ-WUI-STRUCTURED-001` in real runs because the UI/API are not actually projected from stored authoritative structured outputs.
Suggested fix: make `extractStructuredResult()` first read `output.structuredResult` and fall back to legacy top-level fields only for compatibility, then recompute convergence from that normalized source.

3. The declared reaction-input model is only partially implemented: prior task outputs and declared selectors are not actually propagated into failed-check context.
File: `packages/cli/src/workflow/domain/index.ts:143-182`, `packages/cli/src/workflow/check-stage-runner.ts:103-114`, `packages/cli/src/workflow/check-stage-runner.ts:469-470`
Evidence: `buildFailedCheckContext()` supports `priorTaskOutputs`, but `runFixTask()` calls it with only `failedCheck`; no selected task outputs are passed. The stage definition declares `inputFrom` on `review-passed` repair policy (`packages/cli/src/workflow/domain/index.ts:845-849`, `872-876`), but the execution path does not evaluate those selectors. The resulting repair prompt includes blocking items and snapshot only, not the broader explicit bounded context promised by `REQ-WD-001` and `REQ-WR-001`.
Suggested fix: resolve `inputFrom` selectors when scheduling the reaction, assemble `priorTaskOutputs`/artifact refs from the authoritative stage data, and pass that assembled context into `buildFailedCheckContext()` and the repair prompt.

## Open Questions

- None.

## Spec Compliance

- PASS: Generic workflow result types exist without review-specific core entities. Evidence: `packages/cli/src/types/workflow-results.ts:3-100`.
- FAIL: Authoritative structured outputs are not reliably projected into stage convergence state because stage-state parsing ignores `output.structuredResult`. Evidence: `packages/cli/src/services/stage-state-service.ts:550-649`.
- PASS: Built-in judgment tasks declare shared promise-marker result contracts. Evidence: `packages/cli/src/workflow/domain/index.ts:712-724`, `760`, `834`.
- FAIL: Reaction outputs are not recorded from real repair-task executions because the agent-session handler never returns attempted/resolved/unresolved IDs. Evidence: `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:85-103`, `packages/cli/src/workflow/convergence.ts:67-104`.
- PASS: Review and self-review checks use the shared parser and produce check errors for malformed output. Evidence: `packages/cli/src/workflow/checks/review-passed-check.ts:33-50`, `packages/cli/src/workflow/checks/self-review-passed-check.ts:14-21`, `packages/cli/src/workflow/result-contracts.ts:137-212`.
- PASS: Review prompt requires comprehensive pass, explicit marker, structured categories, and bounded self-repair policy. Evidence: `packages/cli/src/agents/prompts/review.md:7-132`.
- FAIL: Reaction input selectors are declared but not fully executed; selected prior task outputs are dropped. Evidence: `packages/cli/src/workflow/domain/index.ts:845-849`, `872-876`; `packages/cli/src/workflow/check-stage-runner.ts:103-114`.
- PASS: UI component renders the generic convergence fields when they are present. Evidence: `packages/cli/web/src/components/WorkflowConvergencePanel.tsx:7-90`.
- WARNING: Tests cover parser, convergence helper logic, API projection, and UI rendering, but the repair-task persistence gap is not exercised end-to-end. Evidence: `packages/cli/tests/workflow/result-contracts.test.ts:21-306`, `packages/cli/tests/workflow/convergence-recheck.test.ts:54-608`, `packages/cli/tests/api/convergence-state-api.test.ts:124-514`.

## Result

Overall result: FAIL. The implementation introduces the right types, parser, prompts, and UI surface, but the structured convergence loop is broken in real runtime flow because reaction-task outputs are not persisted in the required shape and stage-state projection ignores the authoritative nested structured result payload.

<promise>FAIL</promise>
