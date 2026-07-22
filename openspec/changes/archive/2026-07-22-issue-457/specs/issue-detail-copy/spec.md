### Requirement: Single-language UI copy
All user-visible copy rendered on the issue detail page MUST be in English. The page MUST NOT render non-English (Chinese) strings in any state, including the PR delivery indicator and the branch bar upstream-unknown state.

#### Scenario: PR delivery indicator renders English copy
- **WHEN** a completed merged pull-request delivery task exists in the workflow timeline
- **THEN** the PR delivery indicator MUST render English copy identifying the merged pull request (referencing the PR number) and MUST NOT render the Chinese string "经由 PR #N 合并"

#### Scenario: Branch bar upstream-unknown state renders English copy
- **WHEN** the branch bar cannot determine upstream status
- **THEN** the upstream-unknown message MUST render in English conveying that upstream could not be checked, and MUST NOT render the Chinese string "未能检查上游"

### Requirement: Truthful session usage copy
Session usage copy on the issue detail page MUST be truthful about what is known. The sessions panel MUST NOT assert that a session has no usage when usage data or other evidence of activity exists.

#### Scenario: Session row with token usage shows the token figure
- **WHEN** a session has token usage data (total, input, or output tokens)
- **THEN** the session row MUST display the available token figure(s) instead of any "no usage" placeholder

#### Scenario: Session row without token figures but with activity does not claim no usage
- **WHEN** a session has no token usage figures but is otherwise known to have run or produced artifacts
- **THEN** the session row MUST NOT display "No usage yet"; it MUST show copy consistent with what is known (for example that usage figures are unavailable)
