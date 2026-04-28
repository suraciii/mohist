## ADDED Requirements

### Requirement: Review Result parsing
The system SHALL parse `## Result: PASS|FAIL` from review.md using regex `/\x23\x23\s*Result\s*:\s*(PASS|FAIL)/im`. When no match is found, the system SHALL treat the result as FAIL. The function SHALL be named `parseResult` (replacing the legacy `parseVerdict`).

#### Scenario: PASS result parsed correctly
- **WHEN** review.md contains `## Result: PASS`
- **THEN** `parseResult` returns `'PASS'`

#### Scenario: FAIL result parsed correctly
- **WHEN** review.md contains `## Result: FAIL`
- **THEN** `parseResult` returns `'FAIL'`

#### Scenario: Case-insensitive matching
- **WHEN** review.md contains `## result: pass`
- **THEN** `parseResult` returns `'PASS'`

#### Scenario: No result header treated as FAIL
- **WHEN** review.md does not contain a `## Result:` header
- **THEN** `parseResult` returns `null`
- **AND** the system SHALL treat this as FAIL

### Requirement: Fix Suggestions extraction
The system SHALL extract the Fix Suggestions section from review.md, starting from `## Fix Suggestions` to the end of the file. This content SHALL be passed as the core input to the auto-fix agent prompt.

#### Scenario: Fix Suggestions section extracted
- **WHEN** review.md contains a `## Fix Suggestions` section with numbered items
- **THEN** the system extracts all content from `## Fix Suggestions` to end of file
- **AND** the extracted text includes the `[file:line]` references and fix descriptions

#### Scenario: No Fix Suggestions section
- **WHEN** review.md does not contain `## Fix Suggestions`
- **THEN** the extracted suggestions SHALL be empty string
- **AND** the system SHALL treat this as non-auto-fixable FAIL

### Requirement: Auto-fix loop execution
When the review stage self-check round completes and the parsed Result is FAIL, the system SHALL enter an auto-fix loop. The loop SHALL spawn an auto-fix agent round followed by a re-verify round, up to a maximum of 2 attempts.

#### Scenario: PASS skips auto-fix
- **WHEN** self-check round completes and `parseResult(reviewReport)` returns `'PASS'`
- **THEN** the review stage returns success with `requiresApproval: true`
- **AND** no auto-fix rounds are spawned

#### Scenario: FAIL enters auto-fix loop attempt 1
- **WHEN** self-check round completes and `parseResult(reviewReport)` returns `'FAIL'`
- **THEN** the system spawns an auto-fix agent round (roundType: `'auto-fix'`, roundIndex: 2)
- **AND** the auto-fix prompt includes the full review report and extracted Fix Suggestions

#### Scenario: Auto-fix round failure counts as attempt
- **WHEN** the auto-fix agent round fails (ACP connection error, non-zero exit)
- **THEN** the system SHALL count this as one failed attempt
- **AND** continue the loop if attempts remain
- **AND** NOT immediately return an error

### Requirement: Re-verify is full re-review
After each auto-fix round, the system SHALL spawn a full re-review round (not targeted verification) on a new ACP connection. The re-verify round SHALL produce an updated review.md.

#### Scenario: Re-verify spawns after successful auto-fix
- **WHEN** auto-fix round completes successfully
- **THEN** the system spawns a re-verify agent round (roundType: `'re-verify'`, roundIndex: 3)
- **AND** the re-verify prompt instructs the agent to perform a complete review
- **AND** a new ACP connection is created (not reusing the auto-fix connection)

#### Scenario: Re-verify produces updated review.md
- **WHEN** re-verify round completes
- **THEN** the system reads the updated review.md
- **AND** parses its Result to determine PASS or FAIL

### Requirement: Auto-fix loop PASS outcome
When the re-verify round produces a PASS result after auto-fix, the system SHALL add an issue comment documenting the fixes applied and proceed to awaiting-user.

#### Scenario: Auto-fix succeeds on first attempt
- **WHEN** re-verify round produces Result: PASS after the first auto-fix attempt
- **THEN** the system adds an issue comment containing the original Fix Suggestions
- **AND** returns success with `requiresApproval: true`

#### Scenario: Auto-fix succeeds on second attempt
- **WHEN** the first auto-fix + re-verify still produces FAIL
- **AND** the second auto-fix + re-verify produces PASS
- **THEN** the system adds an issue comment documenting fixes
- **AND** returns success with `requiresApproval: true`

### Requirement: Auto-fix loop exhaustion and escalation
When the auto-fix loop exhausts the maximum of 2 attempts without achieving PASS, the system SHALL escalate back to the build stage. The escalation SHALL set a `no-auto-fix` checkpoint marker so the subsequent review pass skips auto-fix.

#### Scenario: Max attempts exhausted
- **WHEN** 2 auto-fix + re-verify cycles both produce FAIL
- **THEN** the system returns a StageResult with `success: false`
- **AND** `escalateToStage` set to `'build'`
- **AND** a `no-auto-fix` checkpoint is recorded for the `review` stage

#### Scenario: Second review pass skips auto-fix
- **WHEN** the review stage runs again after escalation from build
- **AND** the `no-auto-fix` checkpoint exists for this issue's review stage
- **THEN** self-check Result: FAIL SHALL NOT trigger the auto-fix loop
- **AND** the system returns success with `requiresApproval: true` directly

### Requirement: Auto-fix and re-verify prompts
The system SHALL provide `buildAutoFixPrompt` and `buildReVerifyPrompt` functions in `artifact-prompt.ts`, backed by prompt template files `auto-fix.md` and `re-verify.md`.

#### Scenario: Auto-fix prompt includes review context
- **WHEN** `buildAutoFixPrompt` is called with the review report and fix suggestions
- **THEN** the prompt includes the full review report
- **AND** explicitly references the Fix Suggestions section
- **AND** instructs the agent to fix each suggestion one by one
- **AND** instructs the agent to run build verification after fixes
- **AND** does NOT instruct the agent to rewrite review.md

#### Scenario: Re-verify prompt requests full review
- **WHEN** `buildReVerifyPrompt` is called
- **THEN** the prompt instructs the agent to perform a complete review
- **AND** does NOT restrict verification to previously failed items only

### Requirement: Review stage function decomposition
`runPipelineReviewStage` SHALL be decomposed into focused helper methods to keep each function under ~100 lines. The decomposition SHALL include separate methods for: result parsing + branching, auto-fix loop execution, and checkpoint checking.

#### Scenario: Main method delegates to helpers
- **WHEN** `runPipelineReviewStage` is called
- **THEN** it delegates to extracted helper methods for auto-fix logic
- **AND** each helper method has a single responsibility

### Requirement: Verdict to Result terminology migration
The system SHALL rename all instances of `Verdict` to `Result` in: the regex constant (`VERDICT_RE` → `RESULT_RE`), the parse function (`parseVerdict` → `parseResult`), review prompt templates (header `## Verdict:` → `## Result:`), and SSE event documentation.

#### Scenario: New review reports use Result header
- **WHEN** a review agent generates a report using the updated prompt template
- **THEN** the report contains `## Result: PASS|FAIL` (not `## Verdict:`)

#### Scenario: Legacy Verdict header still parsed
- **WHEN** a review.md from a previous version contains `## Verdict: FAIL`
- **THEN** the system SHALL still parse it as FAIL for backward compatibility
- **AND** log a deprecation warning
