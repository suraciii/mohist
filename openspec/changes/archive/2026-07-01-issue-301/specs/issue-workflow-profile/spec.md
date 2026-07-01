## MODIFIED Requirements

### Requirement: Single source of truth for issue workflow profile

An issue SHALL have exactly one workflow profile selection that is the single source of truth across every read surface. The issue detail read model, the issue list read model, the workflow-profile endpoint response, and `mo issue show` SHALL all project the identical effective `workflowProfileId`. When no issue-level selection is persisted, the effective profile SHALL be resolved by the cascade issue-custom → project-default → system-default, SKIPPING any profile that is on the project's disabled-profile blacklist. `mohist/local` SHALL NOT be an unconditional fallback — the system-default step resolves to the first enabled system profile, and when no enabled profile exists the issue creation SHALL be rejected with an actionable error rather than resolving to a disabled default. No read surface SHALL independently invent or hardcode a default independent of this resolution.

#### Scenario: All read surfaces agree after create with GitHub PR profile

- **WHEN** an issue is created with workflow profile `mohist/github-pr`
- **THEN** the issue detail read model SHALL report `workflowProfileId: "mohist/github-pr"`
- **AND** the issue list read model SHALL report `workflowProfileId: "mohist/github-pr"`
- **AND** the workflow-profile endpoint SHALL report the same profile id
- **AND** `mo issue show <number>` SHALL display `mohist/github-pr`

#### Scenario: Read surfaces agree after update to GitHub PR profile

- **WHEN** a backlog issue whose profile is `mohist/local` is updated to `mohist/github-pr`
- **THEN** the issue detail, list, and workflow-profile endpoint SHALL all report `workflowProfileId: "mohist/github-pr"` in the same response cycle

#### Scenario: No issue-level selection inherits default

- **WHEN** an issue is created without an explicit workflow profile selection in a project that has at least one enabled profile
- **THEN** the effective `workflowProfileId` SHALL resolve to the project default when it is enabled, otherwise to the first enabled system profile
- **AND** any profile on the project's disabled-profile blacklist SHALL be skipped in the resolution cascade
- **AND** every read surface SHALL report that same resolved value

#### Scenario: Disabled project default is skipped in the cascade

- **WHEN** an issue is created without an explicit workflow profile selection in a project whose project-default profile is on the disabled-profile blacklist
- **AND** the project has at least one other enabled system profile
- **THEN** the effective `workflowProfileId` SHALL resolve to the first enabled system profile
- **AND** the effective profile SHALL NOT be the disabled project default

#### Scenario: No enabled profile blocks issue creation

- **WHEN** an issue is created without an explicit workflow profile selection in a project whose disabled-profile blacklist contains every system profile
- **THEN** the creation SHALL be rejected with an actionable error instructing the operator to enable a workflow first
- **AND** no read surface SHALL resolve the effective profile to a disabled profile
- **AND** no issue SHALL be persisted
