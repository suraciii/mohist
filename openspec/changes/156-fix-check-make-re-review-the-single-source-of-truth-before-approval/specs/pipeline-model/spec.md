## ADDED Requirements

### Requirement: Re-review regenerates current snapshot

After check-stage AI review auto-fix changes the worktree, Mohist SHALL regenerate the AI review artifacts from the fixed current snapshot before evaluating the re-review verdict. Existing `review.md` or `review-self-check.md` artifacts from before the fix SHALL NOT be reused as current recheck truth.

#### Scenario: Fix invalidates stale review artifacts

- **WHEN** `ai-review` fails
- **AND** `fix-review-findings` modifies code or review-relevant artifacts
- **THEN** the check stage SHALL invalidate or bypass checkpoint skips for `review.md` and `review-self-check.md`
- **AND** it SHALL regenerate those artifacts before running `AiReviewCheck` again

#### Scenario: Recheck reads regenerated report

- **WHEN** re-review runs after `fix-review-findings`
- **THEN** `AiReviewCheck` SHALL parse the regenerated `review.md`
- **AND** it SHALL NOT parse the pre-fix `review.md` as the current recheck truth

#### Scenario: Regenerated artifacts and verdict agree

- **WHEN** re-review completes
- **THEN** `review.md`, `review-self-check.md`, and the current persisted `ai-review` verdict SHALL describe the same final PASS or FAIL result
- **AND** check-stage approval SHALL only be reachable from the converged PASS case
