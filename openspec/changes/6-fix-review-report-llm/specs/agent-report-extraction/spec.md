## ADDED Requirements

### Requirement: Stage report extracted from agent output file

After a stage (plan, review) completes, the workflow controller SHALL read the agent's structured output file from disk as the stage report, falling back to `result.text` only when the file does not exist.

**Plan stage**: read `{changeDir}/self-review.md`
**Review stage**: read `{changeDir}/review.md`

#### Scenario: Review stage with review.md present

- **WHEN** the review stage agent completes successfully
- **AND** `{changeDir}/review.md` exists on disk
- **THEN** the workflow controller SHALL read `review.md` content as `reviewReport`
- **AND** SHALL NOT use `result.text` from the ACP session

#### Scenario: Plan stage self-review with self-review.md present

- **WHEN** the plan stage self-review agent completes successfully
- **AND** `{changeDir}/self-review.md` exists on disk
- **THEN** the workflow controller SHALL read `self-review.md` content as `selfReviewNotes`
- **AND** SHALL NOT use `result.text` from the ACP session

#### Scenario: Output file missing, fallback to result.text

- **WHEN** the stage agent completes successfully
- **AND** the expected output file does NOT exist on disk
- **THEN** the workflow controller SHALL fall back to `result.text` as the report

#### Scenario: Output file exists but is empty

- **WHEN** the stage agent completes successfully
- **AND** the expected output file exists but is empty (0 bytes)
- **THEN** the workflow controller SHALL fall back to `result.text` as the report
