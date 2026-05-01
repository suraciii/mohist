## Review Summary

Self-review of all artifacts for Issue #116 (Per-Issue Task Queue). 4 issues found and fixed.

## Issues Found

### Issue 1: Critical — `/retry` and `/rerun` breaking gap (tasks.json)

**Problem:** T-006 excluded `/retry` and `/rerun` from scope, but T-008 removes `resumePipeline()` and `isRunning()` methods that these endpoints call. After T-008, both endpoints would have broken references.

**Evidence:** `/retry` calls `agentRunner.resumePipeline()` (issues.ts:2564) and `agentRunner.isRunning()` (issues.ts:2527). `/rerun` calls `agentRunner.isRunning()` (issues.ts:2648) and `agentRunner.resumePipeline()` (issues.ts:2699).

**Fix:** Expanded T-006 to include `/retry` and `/rerun` conversion. These endpoints keep their pre-processing (synchronous DB operations like checkpoint checks, status resets) in the handler, only replacing the final `resumePipeline()` call with `enqueue('resume-pipeline')`. `/retry` may return 200 without enqueue when resetting to backlog (no pipeline needed).

### Issue 2: Missing `propose.ts` scope in T-006 (tasks.json)

**Problem:** T-006 output listed only `packages/cli/src/api/issues.ts`, but `api/propose.ts` has its own `isRunning()` (line 96), `getMaxConcurrentAgents()` (line 105), and `startPipeline()` (line 120) calls that need conversion.

**Fix:** Updated T-006 description and notes to explicitly mention `api/propose.ts` conversion. Added acceptance criterion for `/propose` enqueue in propose.ts.

### Issue 3: Missing validation acceptance criterion in T-002 (tasks.json)

**Problem:** The `issue-task-queue` spec has an "Enqueue validation" requirement with a scenario: "Enqueue with invalid issueId → throw error". T-002's acceptance criteria didn't cover this.

**Fix:** Added "enqueue with invalid issueId throws an error (enqueue rejected)" to T-002 acceptance criteria.

### Issue 4: `/approve` is a new handler, not a conversion (tasks.json)

**Problem:** The existing `http-api` spec says `/approve` returns 404 (it was previously removed). T-006's description implied it was converting an existing handler, but it actually needs to create a new handler from scratch.

**Fix:** Updated T-006 description to clarify `/approve` is a NEW handler creation. Updated acceptance criterion to "POST /approve → NEW handler created, enqueue('resume-pipeline'), returns 202".

## Validation Results

- **Completeness:** All 8 spec requirements across 4 spec files are covered by tasks. All proposal capabilities have corresponding specs.
- **Consistency:** Spec references in tasks match actual spec content. Design decisions (D1-D7) all have corresponding task implementations.
- **DAG validation:** All dependsOn reference existing IDs with strictly lower priority. No cycles. All non-first tasks have at least one dependency.
- **Naming:** Capability names consistent between proposal, spec directory names, and task spec references.

## Files Modified

- `tasks.json` — 3 edits: T-002 acceptance criteria, T-006 full replacement, T-008 acceptance criteria
