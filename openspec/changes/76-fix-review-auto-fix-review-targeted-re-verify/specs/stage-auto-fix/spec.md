## ADDED Requirements

### Requirement: Verdict parsing after self-check

The system SHALL parse the PASS/FAIL verdict from the self-check output file (`review.md` for review stage, `self-review.md` for plan stage) after the self-check round completes. The verdict determines whether the stage proceeds to awaiting-user or enters auto-fix.

#### Scenario: Review self-check returns PASS

- **WHEN** the review stage self-check round completes
- **AND** the `review.md` file contains `## Verdict: PASS`
- **THEN** the system SHALL skip auto-fix
- **AND** the stage SHALL return success with `requiresApproval: true`

#### Scenario: Review self-check returns FAIL

- **WHEN** the review stage self-check round completes
- **AND** the `review.md` file contains `## Verdict: FAIL`
- **THEN** the system SHALL enter the auto-fix flow

#### Scenario: Plan self-review returns PASS

- **WHEN** the plan stage self-review round completes
- **AND** the `self-review.md` file contains `## Verdict: PASS`
- **THEN** the system SHALL skip auto-fix
- **AND** the stage SHALL return success with `requiresApproval: true`

#### Scenario: Plan self-review returns FAIL

- **WHEN** the plan stage self-review round completes
- **AND** the `self-review.md` file contains `## Verdict: FAIL`
- **THEN** the system SHALL enter the auto-fix flow

#### Scenario: Verdict missing or unparseable

- **WHEN** the self-check/self-review output file exists but does not contain a `## Verdict: PASS` or `## Verdict: FAIL` line
- **THEN** the system SHALL treat the verdict as FAIL
- **AND** the system SHALL enter the auto-fix flow

### Requirement: Auto-fix on same ACP connection

The system SHALL send an auto-fix prompt on the **same** ACP connection used for the self-check round. The auto-fix prompt SHALL instruct the agent to fix the issues identified in the self-check report.

#### Scenario: Auto-fix prompt sent after review FAIL

- **WHEN** the review self-check verdict is FAIL
- **THEN** the system SHALL send an auto-fix prompt on the current ACP connection
- **AND** the prompt SHALL include the content of `review.md` (the failing report with fix suggestions)
- **AND** the prompt SHALL instruct the agent to apply all fix suggestions from the report

#### Scenario: Auto-fix prompt sent after plan FAIL

- **WHEN** the plan self-review verdict is FAIL
- **THEN** the system SHALL send an auto-fix prompt on the current ACP connection
- **AND** the prompt SHALL include the content of `self-review.md` (the failing report)
- **AND** the prompt SHALL instruct the agent to fix the issues identified in the self-review

#### Scenario: Auto-fix prompt fails

- **WHEN** the auto-fix prompt returns a non-success result from the ACP connection
- **THEN** the system SHALL close the connection
- **AND** the stage SHALL return success with `requiresApproval: true`
- **AND** the stage message SHALL note that auto-fix failed

### Requirement: Full re-check on new ACP connection

After auto-fix completes, the system SHALL close the current ACP connection and open a **new** ACP connection for an unbiased full re-check. The re-check SHALL run the complete review (or self-review) + self-check sequence — not a targeted re-verify of only the fixed items.

#### Scenario: Full re-review after review auto-fix

- **WHEN** auto-fix completes successfully for the review stage
- **THEN** the system SHALL close the current ACP connection
- **AND** open a new ACP connection
- **AND** re-run the full review prompt (`buildReviewerPrompt`)
- **AND** re-run the full self-check prompt (`buildReviewSelfCheckPrompt`)
- **AND** parse the verdict from the new `review.md`

#### Scenario: Full re-self-review after plan auto-fix

- **WHEN** auto-fix completes successfully for the plan stage
- **THEN** the system SHALL close the current ACP connection
- **AND** open a new ACP connection
- **AND** re-run the full self-review prompt (`buildSelfReviewPrompt`)
- **AND** parse the verdict from the new `self-review.md`

### Requirement: Single auto-fix attempt

The system SHALL attempt auto-fix exactly once. The re-check after auto-fix determines the final outcome — no further auto-fix rounds occur.

#### Scenario: Re-check PASS after auto-fix

- **WHEN** the full re-check after auto-fix produces Verdict PASS
- **THEN** the system SHALL close the ACP connection
- **AND** the stage SHALL return success with `requiresApproval: true`
- **AND** the stage message SHALL note that auto-fix succeeded

#### Scenario: Re-check FAIL after auto-fix

- **WHEN** the full re-check after auto-fix produces Verdict FAIL
- **THEN** the system SHALL close the ACP connection
- **AND** the stage SHALL return success with `requiresApproval: true`
- **AND** the stage message SHALL note that auto-fix was attempted but re-check still FAIL

### Requirement: No escalation on auto-fix failure

When auto-fix is attempted and the re-check still FAIL, the system SHALL await user decision — it SHALL NOT escalate to a different stage (e.g., restart build) or retry.

#### Scenario: Auto-fix exhausted does not escalate

- **WHEN** the re-check after auto-fix produces Verdict FAIL
- **THEN** the system SHALL NOT restart the build stage
- **AND** the system SHALL NOT restart the plan stage
- **AND** the system SHALL return `requiresApproval: true` to let the user decide

### Requirement: Auto-fix event emission

The system SHALL emit events for each auto-fix round so the UI can display progress.

#### Scenario: Events emitted during review auto-fix

- **WHEN** the auto-fix round starts in the review stage
- **THEN** the system SHALL emit an event with `roundType: 'auto-fix'`
- **WHEN** the re-review round starts
- **THEN** the system SHALL emit an event with `roundType: 're-review'`
- **WHEN** the re-self-check round starts
- **THEN** the system SHALL emit an event with `roundType: 're-review-self-check'`

#### Scenario: Events emitted during plan auto-fix

- **WHEN** the auto-fix round starts in the plan stage
- **THEN** the system SHALL emit an event with `roundType: 'auto-fix'`
- **WHEN** the re-self-review round starts
- **THEN** the system SHALL emit an event with `roundType: 're-self-review'`
