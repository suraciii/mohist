## Why

The check stage currently exposes internal validation names and treats `ai-review` as both work and verification, which makes the approval surface harder to understand and weakens the boundary between tasks that produce evidence and checks that judge it. This change is needed now so users can approve against two clear outcomes: the current code passed AI review, and the reviewed code can be merged.

## What Changes

- Reframe `ai-review` as the check-stage task that reviews the implementation, may repair simple issues, regenerates the final `review.md`, and guarantees the review artifact exists with a machine-readable verdict.
- Replace user-visible check-stage checks with `review-passed`, `merge-ready`, and `user-approval`.
- Make `review-passed` a read-only verifier that parses the final review verdict and decides whether review findings require a dynamically generated repair task.
- Make `merge-ready` the user-visible mergeability check for the reviewed candidate snapshot, including conflict or rebase handling before approval.
- Invalidate the current review result and rerun `ai-review` whenever merge-readiness work changes the candidate code snapshot.
- Keep health gates, integration preview evidence, review artifact format validation, and other implementation details internal to task execution or check evidence instead of exposing them as separate user-facing checks.
- Update UI/API presentation so users no longer need to understand internal names such as `health:check`, `merge-readiness`, or `integration-health-gate-preview`.

## Capabilities

### New Capabilities


### Modified Capabilities

- workflow-engine
- pipeline-model
- http-api
- web-ui

## Impact

- Affects `packages/cli/src/workflow/check-stage-runner.ts`, where check-stage pre-task checks, review artifact generation, AI-review repair flow, and default check ordering are currently defined.
- Affects `packages/cli/src/workflow/base-stage-runner.ts` and `packages/cli/src/workflow/stage-context.ts`, where check results are persisted, AI review truth is made authoritative, failed checks map to repair tasks, and approval output is built.
- Affects check implementations under `packages/cli/src/workflow/checks/`, especially replacing user-visible `ai-review`, `merge-readiness`, `integration-health-gate-preview`, and `health:check` semantics with the simplified `review-passed` and `merge-ready` model.
- Affects `packages/cli/src/workflow/review-fix-task.ts` and related repair-task orchestration so review failures create concrete repair work only when findings require it.
- Affects persisted check-suite defaults and types in `packages/cli/src/db/check-suite-repo.ts` and `packages/cli/src/types/index.ts` so visible check state matches the simplified model.
- Affects issue and approval APIs in `packages/cli/src/api/issues.ts`, including approval gating against the current code snapshot's final review verdict and merge-ready state.
- Affects CLI and Web UI rendering of check-stage progress/results so users see `ai-review` as work and only `review-passed`, `merge-ready`, and approval as decision points.
- No new external runtime dependencies are expected.
