### Requirement: Only the Owner may change the access policy and allowlist

The access policy and the allowlist SHALL be modifiable only by the Connection Owner through an explicit Manage access operation. A Slack message SHALL NOT change the policy or the allowlist, and a Slack member identity SHALL NOT be treated as a Mohist administrator identity or used to switch Project, Agent, or access scope.

#### Scenario: The Owner changes the policy via Manage access

- **WHEN** the Connection Owner performs a Manage access operation to set the policy to `allowlist`
- **THEN** the policy is changed to `allowlist`

#### Scenario: A Slack message does not change the policy

- **WHEN** a member sends a Slack message containing a request to change the access policy
- **THEN** the access policy is not changed

### Requirement: The Owner selects among three policies with Owner-only as the default

Manage access SHALL allow the Owner to set the policy to `owner_only`, `allowlist`, or `anyone`. A newly created Connection SHALL start with the `owner_only` policy.

#### Scenario: A new Connection defaults to Owner-only

- **WHEN** a new Connection is created
- **THEN** its access policy is `owner_only`

#### Scenario: The Owner sets each available policy

- **WHEN** the Owner sets the policy in turn to `allowlist` and then to `anyone`
- **THEN** the policy reflects each selection

### Requirement: Allowlist members are managed by stable identity and presented by recognizable member information

Adding or removing allowlist members SHALL be performed against the member's stable Slack identity. The management surface MAY present members by name and avatar to let the Owner recognize and select members, but display name and avatar SHALL NOT be the authorization identity. The Owner SHALL always be present in the allowlist and SHALL NOT be removable.

#### Scenario: A member is added to the allowlist by stable identity

- **WHEN** the Owner adds a workspace member by selecting the member's name and avatar, which resolve to the member's stable Slack identity
- **THEN** that stable identity is recorded as an allowlist member

#### Scenario: Display name and avatar are presentation only

- **WHEN** a member later changes their display name or avatar
- **THEN** their allowlist membership is unaffected because it is recorded by stable identity

#### Scenario: The Owner cannot be removed from the allowlist

- **WHEN** the Owner attempts to remove their own stable identity from the allowlist
- **THEN** the Owner remains present and authorized in the allowlist

### Requirement: Selecting anyone discloses that it grants the Agent's configured execution authority

Before the Owner applies an `anyone` policy, the management surface SHALL disclose that invoking the Bot is equivalent to exercising the Agent's already-configured repository-write, tool, and credential authority, and that setting `anyone` grants that authority to channel members who satisfy the policy. The disclosure SHALL be presented before the change takes effect.

#### Scenario: The Owner is shown the execution-authority disclosure before applying anyone

- **WHEN** the Owner begins to apply an `anyone` policy
- **THEN** the management surface presents a disclosure stating that the change grants the Agent's configured repository-write, tool, and credential authority to qualifying channel members
- **AND** the change does not take effect until the Owner proceeds past the disclosure

### Requirement: The CLI replaces the allowlist as a whole and rejects incompatible combinations

The CLI Manage access operation SHALL accept a policy selection and, for `allowlist`, a repeatable allow-member argument whose stable identities replace the full allowlist excluding the Owner. The Owner SHALL be re-added automatically after a replace and SHALL NOT be removable. Supplying an allow-member together with `owner_only` or `anyone` SHALL be rejected before any mutation is applied.

#### Scenario: The CLI replaces the allowlist with the supplied members

- **WHEN** the Owner runs the CLI Manage access with `--access-policy allowlist` and several repeatable `--allow-member` identities
- **THEN** the allowlist is set to exactly those members plus the Owner
- **AND** any previously listed member not supplied is removed

#### Scenario: The Owner is re-added automatically after a replace

- **WHEN** the Owner replaces the allowlist without including their own identity in the supplied members
- **THEN** the resulting allowlist still contains the Owner

#### Scenario: An allow-member with Owner-only or Anyone is rejected before mutation

- **WHEN** the Owner runs the CLI Manage access with `--access-policy owner_only --allow-member <id>` or `--access-policy anyone --allow-member <id>`
- **THEN** the request is rejected and no policy or allowlist change is applied

### Requirement: A policy or allowlist change is durable and effective without a restart

A change to the access policy or the allowlist SHALL be persisted and SHALL take effect for the next received input without requiring a process restart or reconnection.

#### Scenario: A change applies to the next received input without a restart

- **WHEN** the Owner changes the policy or allowlist and a subsequent channel input arrives
- **THEN** the new input is evaluated against the changed policy or allowlist without any restart
