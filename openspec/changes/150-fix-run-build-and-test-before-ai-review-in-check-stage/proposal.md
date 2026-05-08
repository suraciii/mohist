## Why

The check stage can currently produce AI review artifacts before build and test verification runs, which makes the review and subsequent approval request untrustworthy when the implementation does not compile or tests fail. Approval should only be requested after mechanical verification passes and the AI review has evaluated the verified implementation.

## What Changes

- Reorder the check stage so `BuildTestCheck` runs before generating `review.md` or `review-self-check.md`.
- Keep the existing `checks.buildTest` command and timeout configuration as the source of build/test behavior.
- If build/test fails, run the existing configured autofix attempts and rerun the same build/test command.
- If build/test still fails after the maximum autofix attempts, fail the check stage with a concise failure summary and useful log excerpt.
- Do not generate AI review artifacts and do not request user approval when build/test verification fails.
- Generate AI review artifacts, run AI review checks, and request user approval only after build/test verification passes.
- Preserve existing AI review behavior after mechanical verification has passed.

## Capabilities

### New Capabilities


### Modified Capabilities

- workflow-definition

## Impact

- Affects `packages/cli/src/workflow/check-stage-runner.ts`, where review artifact generation currently happens before the inherited check chain runs.
- Affects `packages/cli/src/workflow/base-stage-runner.ts` check orchestration if the implementation needs a way to run mechanical checks before stage tasks or split check-stage task execution into ordered phases.
- Affects `packages/cli/src/workflow/checks/build-test-check.ts` only as needed to ensure failed build/test results expose a concise summary and useful log excerpt.
- Affects check-stage event/task reporting for the order of visible work: build/test must be reported before AI review and approval.
- No changes expected to workflow configuration schema; existing `checks.buildTest` configuration remains supported.
- No new runtime dependencies are expected.
