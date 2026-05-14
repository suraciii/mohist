## MODIFIED Requirements

### Requirement: REQ-CLI-ISSUE-LIST-TRIAGE-FILTERS Issue list supports triage-oriented scope filters

The CLI and issue list API SHALL support triage-oriented issue scopes for active pipeline work, multiple stages, and attention items without adding workflow stages or identity ownership concepts.

#### Scenario: Active alias lists pipeline work only
- **WHEN** the user runs `mo issue list -s active`
- **THEN** the command SHALL list issues in `plan`, `build`, `check`, or `integrate` that are not closed or completed
- **AND** it SHALL NOT list backlog issues solely because their status is `active`

#### Scenario: Multi-stage filter uses OR semantics
- **WHEN** the user runs `mo issue list -s build,check`
- **THEN** the command SHALL list issues whose stage is `build` or `check`

#### Scenario: Invalid stage fails clearly
- **WHEN** the user runs `mo issue list -s unknown`
- **THEN** the command SHALL print a clear invalid stage or alias error
- **AND** it SHALL exit with a non-zero status
- **AND** it SHALL NOT silently return an empty list

#### Scenario: Stage filters compose with existing filters
- **WHEN** the user combines stage selection with priority, label, archived, or all filters
- **THEN** stage selection SHALL be applied as OR within the stage set
- **AND** all other filters SHALL be applied with AND semantics

#### Scenario: Attention filter lists user-decision items
- **WHEN** the user runs `mo issue list --attention`
- **THEN** the command SHALL list issues awaiting approval, blocked, interrupted, delivery blocked, integrate failed, or done/completed but not merged
- **AND** it SHALL NOT include normal running or probing issues unless another attention condition is present

#### Scenario: Attention filter composes and has explicit empty state
- **WHEN** the user combines `--attention` with stage, priority, or label filters
- **THEN** the command SHALL apply `--attention` with AND semantics against those filters
- **AND** when no issues match, the command SHALL display a clear attention-specific empty state

#### Scenario: No personal ownership shortcut is added
- **WHEN** the user views `mo issue list --help`
- **THEN** the help SHALL document `--attention`, comma-separated status values, and the `active` alias
- **AND** it SHALL NOT document or expose `--my`

### Requirement: REQ-CLI-ISSUE-SHOW-COMPACT Issue show supports compact output

The CLI SHALL provide a compact issue show mode for quick human-readable status checks while preserving the default full issue detail output.

#### Scenario: Compact show emits one-line summary
- **WHEN** the user runs `mo issue show <id> --compact`
- **THEN** the command SHALL print a single-line summary containing issue number, stage, status, priority, and title
- **AND** the output SHALL be human-readable text, not JSON

#### Scenario: Compact show omits long sections
- **WHEN** the user runs `mo issue show <id> --compact`
- **THEN** the command SHALL NOT output body, comments, stage checks, approval output, session details, or other long detail sections

#### Scenario: Default show remains full detail
- **WHEN** the user runs `mo issue show <id>` without `--compact`
- **THEN** the command SHALL preserve the existing full detail output behavior

### Requirement: REQ-CLI-ISSUE-DIFF-STAT Issue diff supports stat output

The CLI SHALL provide a diff stat mode that reports file-level change scale without printing the full patch and uses the same comparison semantics as full issue diff.

#### Scenario: Diff stat omits patch content
- **WHEN** the user runs `mo issue diff <id> --stat`
- **THEN** the command SHALL print file-level changed-file, addition, and deletion information
- **AND** it SHALL NOT print full patch hunks or `diff --git` patch blocks

#### Scenario: Default diff remains full patch
- **WHEN** the user runs `mo issue diff <id>` without `--stat`
- **THEN** the command SHALL preserve full patch output behavior

#### Scenario: Diff stat shares comparison semantics
- **WHEN** the user compares `mo issue diff <id>` and `mo issue diff <id> --stat`
- **THEN** both commands SHALL use the same base branch, issue branch, and merge-base comparison semantics

#### Scenario: Diff unavailable states are distinct
- **WHEN** issue diff data is unavailable because the issue has not started, the worktree is removed, a branch is missing, or git comparison fails
- **THEN** `mo issue diff <id> --stat` SHALL print clear feedback that distinguishes the reason
- **AND** it SHALL exit with a non-zero status

#### Scenario: No-change diff is explicit
- **WHEN** issue diff data is available but contains no changed files
- **THEN** `mo issue diff <id> --stat` SHALL print a clear no-changes message
- **AND** it SHALL NOT print patch content
