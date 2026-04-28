## ADDED Requirements

### Requirement: Auto-fix loop on Verdict FAIL

After the self-check round produces a `review.md` with Verdict: FAIL, the system SHALL enter an auto-fix loop. The loop SHALL consist of alternating auto-fix rounds (R2, R4, ...) and re-verify rounds (R3, R5, ...). The loop SHALL run at most 2 attempts (1 attempt = 1 auto-fix round + 1 re-verify round).

#### Scenario: Verdict FAIL triggers auto-fix loop

- **WHEN** the self-check round (R1) completes and `review.md` contains `Verdict: FAIL`
- **THEN** the system SHALL start an auto-fix round (R2) with a prompt that includes the Fix Suggestions from `review.md`
- **AND** the auto-fix agent SHALL apply fixes, supplement tests, and run build

#### Scenario: Verdict PASS skips auto-fix

- **WHEN** the self-check round (R1) completes and `review.md` contains `Verdict: PASS`
- **THEN** the system SHALL skip the auto-fix loop entirely
- **AND** proceed directly to awaiting-user

#### Scenario: Auto-fix loop respects max 2 attempts

- **WHEN** the auto-fix loop has completed 2 attempts (4 rounds: R2-R5)
- **AND** the re-verify round still reports `Verdict: FAIL`
- **THEN** the system SHALL stop retrying and escalate

### Requirement: Auto-fix round applies Fix Suggestions

The auto-fix agent round SHALL receive a prompt containing the full `review.md` content with Fix Suggestions. The agent SHALL apply fixes to the codebase, add supplementary tests for fixed issues, and run the project build to verify compilation.

#### Scenario: Auto-fix round receives structured prompt

- **WHEN** an auto-fix round starts
- **THEN** the prompt SHALL include the `review.md` content with all Fix Suggestions
- **AND** the prompt SHALL instruct the agent to fix each suggestion, add tests, and run build

#### Scenario: Auto-fix round fails

- **WHEN** the auto-fix agent round fails (ACP error, timeout, or build failure)
- **THEN** the system SHALL count it as a failed attempt
- **AND** proceed to the next attempt or escalate if max reached

### Requirement: Re-verify round validates fixes

After each auto-fix round, the system SHALL run a re-verify round. The re-verify agent SHALL perform targeted verification of the specific issues listed in the previous `review.md`, run the project build, and update `review.md` with the new verdict.

#### Scenario: Re-verify confirms all fixes

- **WHEN** the re-verify round completes and all previously identified issues are resolved
- **THEN** the re-verify agent SHALL update `review.md` with `Verdict: PASS`
- **AND** the auto-fix loop SHALL terminate successfully

#### Scenario: Re-verify finds remaining issues

- **WHEN** the re-verify round completes and some issues remain unfixed
- **THEN** the re-verify agent SHALL update `review.md` with `Verdict: FAIL` and updated Fix Suggestions
- **AND** the auto-fix loop SHALL retry (if attempts remain) or escalate

#### Scenario: Re-verify targets known issues only

- **WHEN** the re-verify round runs
- **THEN** the prompt SHALL include the list of specific issues from the previous `review.md`
- **AND** the prompt SHALL instruct targeted verification, not a full re-review

### Requirement: Successful auto-fix records fix history

When the auto-fix loop succeeds (re-verify returns PASS), the system SHALL add an issue comment documenting what was fixed.

#### Scenario: Comment added after successful auto-fix

- **WHEN** the auto-fix loop terminates with `Verdict: PASS` after one or more fix attempts
- **THEN** the system SHALL add a comment to the issue summarizing:
  - Number of auto-fix attempts used
  - List of issues that were fixed (from original `review.md` Fix Suggestions)
- **AND** proceed to awaiting-user

### Requirement: Escalation on persistent failure

When the auto-fix loop exhausts its max attempts without success, the system SHALL escalate by routing the issue back to the build stage with a `no-auto-fix` checkpoint marker.

#### Scenario: Escalate back to build stage

- **WHEN** the auto-fix loop exhausts 2 attempts and `review.md` still shows `Verdict: FAIL`
- **THEN** the system SHALL set a checkpoint with key `no-auto-fix` for the issue
- **AND** route the issue back to the build stage
- **AND** the build stage SHALL receive a prompt referencing the review failure and Fix Suggestions

#### Scenario: Second review pass skips auto-fix

- **WHEN** the issue enters the review stage with a `no-auto-fix` checkpoint
- **THEN** the system SHALL skip the auto-fix loop entirely after self-check
- **AND** proceed directly to awaiting-user regardless of Verdict

### Requirement: Checkpoint no-auto-fix marker

The system SHALL use the existing `PipelineCheckpointRepo` to store a `no-auto-fix` marker per issue. This marker SHALL be scoped to the review stage and SHALL prevent auto-fix loops on subsequent review passes.

#### Scenario: Checkpoint written on escalation

- **WHEN** auto-fix escalation occurs
- **THEN** `checkpointRepo.upsert(issueNumber, 'review', ['no-auto-fix'], null)` SHALL be called

#### Scenario: Checkpoint checked before auto-fix loop

- **WHEN** the review stage self-check round produces `Verdict: FAIL`
- **AND** the system is about to enter the auto-fix loop
- **THEN** the system SHALL check `checkpointRepo.get(issueNumber, 'review')`
- **AND** if the checkpoint contains `no-auto-fix`, skip the auto-fix loop

#### Scenario: Checkpoint cleared on pipeline completion

- **WHEN** the pipeline reaches `done` stage
- **THEN** `checkpointRepo.deleteAll(issueNumber)` SHALL clear all checkpoints including `no-auto-fix`
