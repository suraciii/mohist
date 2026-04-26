## ADDED Requirements

### Requirement: Review stage multi-round pipeline

Review stage (`runPipelineReviewStage`) SHALL execute two rounds of `conn.prompt()` on the same ACP connection before closing it:
- **Round 0** (review): Send the reviewer prompt to generate the review report. The agent writes `review.md` to the change directory.
- **Round 1** (self-check): Send a self-check prompt asking the agent to verify the `review.md` report's format, completeness, and quality. The agent SHALL update `review.md` if corrections are needed.

This mirrors the Plan stage pattern where a self-review round follows the artifact generation rounds.

#### Scenario: Successful two-round review
- **WHEN** Review stage starts for issue #5
- **THEN** round 0 emits `plan_round_start` with `roundType: 'review'`, `roundIndex: 0`
- **AND** the reviewer prompt is sent via `conn.prompt()`
- **AND** after round 0 completes, round 1 emits `plan_round_start` with `roundType: 'review-self-check'`, `roundIndex: 1`
- **AND** the self-check prompt is sent via `conn.prompt()`
- **AND** the final `review.md` content (after self-check) is stored as `reviewReport`
- **AND** `conn.close()` is called only after round 1 completes
- **AND** `requiresApproval: true` is returned

#### Scenario: Round 0 succeeds, round 1 self-check fails
- **WHEN** Review stage round 0 (review) succeeds but round 1 (self-check) fails
- **THEN** the stage SHALL return `success: false` with message indicating self-check failure
- **AND** the review report from round 0 SHALL still be read from `review.md` as fallback
- **AND** `requiresApproval: false` is returned (stage did not complete successfully)

#### Scenario: Round 0 fails
- **WHEN** Review stage round 0 (review) fails
- **THEN** the stage SHALL return `success: false` without attempting round 1
- **AND** `requiresApproval: false` is returned

### Requirement: Review self-check prompt

The system SHALL provide a `buildReviewSelfCheckPrompt(issue, changeDir)` function that generates a prompt instructing the agent to:
1. Read the `review.md` file in the change directory
2. Verify the report contains structured review content (not thinking/reasoning process)
3. Verify the report covers the implementation changes in the change directory
4. Rewrite `review.md` with corrections if the format or content is inadequate

#### Scenario: Self-check prompt includes review.md path
- **WHEN** `buildReviewSelfCheckPrompt` is called with changeDir `/path/to/change`
- **THEN** the prompt instructs the agent to read `review.md` from the change directory
- **AND** the prompt instructs the agent to verify and correct the report

### Requirement: Review output validation

After the self-check round completes, `runPipelineReviewStage` SHALL validate the final review report:
- The report (read from `review.md` or fallback `result.text`) SHALL be non-empty.
- If the report is empty after both rounds, the stage SHALL return `success: false`.

#### Scenario: Empty report after self-check
- **WHEN** `review.md` is empty or missing and the self-check round produced no text
- **THEN** the stage returns `success: false` with message "Review report is empty after self-check"

#### Scenario: Valid report after self-check
- **WHEN** `review.md` contains content after self-check round
- **THEN** the stage returns `success: true` with the report content as `reviewReport`
