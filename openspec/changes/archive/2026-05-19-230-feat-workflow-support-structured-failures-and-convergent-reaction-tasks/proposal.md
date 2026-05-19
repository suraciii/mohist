## Why

Mohist can currently enter repeated check-fix-review loops where each pass discovers only one more blocker, leaving users unable to tell whether the workflow is converging or just cycling. The workflow needs a generic structured failure and reaction model so AI judgment tasks can expose bounded actionable items, repair tasks can consume the whole batch, and pass/fail decisions come from explicit machine-readable verdicts rather than prose.

## What Changes

- Add a generic structured result contract for workflow task and check outputs, including reusable `items[]`, verdict, evidence, repair, snapshot, and verification fields without introducing review-specific core entities.
- Make required verdict markers a declared output contract for AI judgment tasks, defaulting to exactly one `<promise>PASS</promise>` or `<promise>FAIL</promise>` marker parsed from a declared output source.
- Treat missing, duplicate, malformed, or undeclared-source verdict markers as clear task/check errors instead of implicit pass/fail decisions.
- Extend task definitions and task outputs so a task can declare limited in-session repair boundaries and record repaired item IDs, changed evidence, and verification results.
- Update built-in review and self-review flows to use the shared generic verdict parser and structured output contract.
- Update the built-in Check review task to perform a comprehensive pass, optionally fix safe local low-risk findings in-session, and report unresolved blockers, non-blocking follow-ups, pre-existing issues, and direct repairs separately.
- Let failed checks pass structured failed context into configured reaction tasks so `fix-review-findings` receives the full blocking item batch instead of scraping unstructured review text.
- Record reaction task attempted, resolved, and unresolved item IDs, then re-run the configured task/check path with verification of known items before considering policy-allowed new blockers.
- Expose generic convergence state in API/UI surfaces: failed check, blocking item count, direct repairs, reaction attempts, resolved/unresolved counts, blocked reason, and non-blocking follow-ups.
- Preserve existing task/check boundaries: tasks may execute and modify artifacts/code; checks remain read-only validators that parse and validate declared outputs.

## Capabilities

### New Capabilities

- workflow-structured-results

### Modified Capabilities

- workflow-definition
- workflow-run
- workflow-engine
- pipeline-model
- web-ui

## Impact

- Workflow runtime types and persistence for `StageTaskResult`, `CheckResult`, WorkflowRun task/check output, failed-check context, repair/reaction metadata, and stage-state projections.
- Workflow definition/configuration loading for task result contracts, declared output sources, task self-repair policy, reaction inputs, retry limits, and invalidation behavior.
- Shared verdict parsing utilities currently in `packages/cli/src/workflow/utils.ts`, including stricter marker counting and source binding for review, self-review, plan-quality, and future custom AI judgment tasks.
- Check and task implementations under `packages/cli/src/workflow/checks/` and `packages/cli/src/workflow/task-runtime/`, especially `review-passed`, `self-review-passed`, `ai-review`, `fix-review-findings`, and repair adapters.
- Built-in agent prompts for review, self-review, review repair, and re-verification so they emit structured item batches, repair evidence, and exactly one verdict marker.
- Stage-state and issue APIs consumed by the web app so structured convergence status and item counts are available without parsing logs or review prose.
- Web UI components such as Issue Detail, Pipeline/Task progress, check repair display, and review summary surfaces so users can see generic convergence evidence rather than review-specific lifecycle primitives.
- Regression coverage for verdict parser errors, declared-source parsing, structured item persistence, reaction input batching, review self-repair boundaries, recheck convergence, and UI display of resolved/unresolved workflow items.
