### Requirement: The Connection carries one access policy that decides who may invoke the Agent in a channel

The Connection SHALL carry exactly one access policy selected from `owner_only`, `allowlist`, and `anyone`. The default policy of a new Connection SHALL be `owner_only`. A channel root mention and a reply in a thread bound to the Agent SHALL be accepted for processing only when, at the moment the input is received, the sender satisfies the policy then in force. The access policy SHALL decide only who may invoke; it SHALL NOT reduce or expand the Agent's configured Runtime, Skills, repository, or tool authority.

#### Scenario: An Owner mention is accepted under the default Owner-only policy

- **WHEN** a new Connection exists with its default policy and the Connection Owner sends a channel root message mentioning the Bot
- **THEN** the message is accepted and work is created for the Agent

#### Scenario: A policy of anyone does not alter the Agent's own configured authority

- **WHEN** the Owner sets the policy to `anyone` and an authorized member invokes the Agent in a channel
- **THEN** the invoked work runs with the Agent's already-configured repository-write, tool, and credential authority
- **AND** the invocation does not add, remove, or replace any of the Agent's Runtime, Skills, repository, or tool configuration

### Requirement: The Owner is always authorized and is an immovable member of the allowlist

Regardless of the access policy in force, the Connection Owner SHALL be authorized to invoke the Agent in a channel. When the policy is `allowlist`, the Owner SHALL always be present in the allowlist and SHALL NOT be removable from it. Adding or removing other allowlist members SHALL NOT remove or displace the Owner.

#### Scenario: The Owner is authorized under every policy

- **WHEN** the Owner invokes the Agent in a channel while the policy is `owner_only`, `allowlist`, or `anyone`
- **THEN** the invocation is accepted

#### Scenario: Removing an allowlist member never removes the Owner

- **WHEN** the Owner removes a listed member from an `allowlist` policy's allowlist
- **THEN** that member is no longer authorized but the Owner remains authorized and present in the allowlist

### Requirement: Allowlist authorization is by stable workspace identity

Under the `allowlist` policy, a channel mention or bound-thread reply SHALL be authorized only when the sender's stable Slack user identity is the Owner or is explicitly listed. A listed member SHALL, at the moment of the invocation, still be a current, valid, regular member of the Connection's install workspace — not a Bot, not deleted, not a guest, and not a restricted or ultra-restricted member, and belonging to the same workspace team. Authorization SHALL NOT be granted by display name, avatar, or message text.

#### Scenario: A listed current regular member is accepted

- **WHEN** the policy is `allowlist` and a member explicitly listed by stable identity sends a channel mention while remaining a current regular member of the install workspace
- **THEN** the invocation is accepted

#### Scenario: A member who is not listed is rejected

- **WHEN** the policy is `allowlist` and a workspace member who is not the Owner and not in the allowlist mentions the Bot in a channel
- **THEN** the invocation is rejected with an actionable reason

#### Scenario: A listed member who has become a guest or has been deleted is rejected

- **WHEN** the policy is `allowlist` and a member whose stable identity is listed has since become a guest, a restricted member, or a deleted member of the workspace
- **THEN** that member's new invocation is rejected

### Requirement: Anyone authorization requires workspace membership and channel visibility of the Bot

Under the `anyone` policy, a channel mention or bound-thread reply SHALL be authorized only when the sender is proven to belong to the App's install workspace as a current, valid, regular member, and the Bot is a member of the channel in which the message was sent so that the Bot can see the sender. A Slack Connect external participant, a guest, a Bot, a deleted member, and any identity that cannot be confirmed SHALL NOT trigger the Agent. The Bot being invited into a private channel SHALL NOT by itself authorize a sender; the sender must still satisfy the workspace-member requirement.

#### Scenario: A workspace regular member in a channel the Bot is in is accepted

- **WHEN** the policy is `anyone` and a current regular member of the install workspace sends a channel mention in a channel where the Bot is a member
- **THEN** the invocation is accepted

#### Scenario: A guest or external participant is rejected

- **WHEN** the policy is `anyone` and a guest, a restricted member, or a Slack Connect external participant sends a channel mention
- **THEN** the invocation is rejected and creates no Agent resources

#### Scenario: A sender in a channel the Bot is not a member of is rejected

- **WHEN** the policy is `anyone` and a workspace regular member sends a channel mention in a channel where the Bot is not a member
- **THEN** the invocation is not authorized

### Requirement: Direct messages remain Owner-only under every policy

Regardless of the access policy in force, a one-to-one direct message SHALL be accepted only when the sender is the Connection Owner. Widening the channel policy to `allowlist` or `anyone` SHALL NOT grant any member invocation authority in a direct message.

#### Scenario: A direct message from a non-Owner is rejected under anyone

- **WHEN** the policy is `anyone` and a member who is not the Owner sends the Bot a direct message
- **THEN** the message is rejected with an actionable reason and creates no Agent resources

### Requirement: An unauthorized invocation creates no Agent resources

A channel mention or bound-thread reply from a sender who does not satisfy the policy in force SHALL be rejected with an actionable reason and SHALL create no AgentJob, no AgentSession, no SessionInput, and no inbox entry.

#### Scenario: An unauthorized channel mention creates no resources

- **WHEN** a sender who does not satisfy the policy in force mentions the Bot in a channel
- **THEN** the message is rejected with an actionable reason
- **AND** no Job, Session, SessionInput, or inbox entry is created

#### Scenario: An unauthorized bound-thread reply creates no resources

- **WHEN** a sender who does not satisfy the policy in force replies in a thread bound to the Agent
- **THEN** the reply is rejected and creates no Job, Session, SessionInput, or inbox entry

### Requirement: Policy changes take effect immediately for new inputs without revoking accepted work

A change to the access policy or the allowlist SHALL apply to every input received after the change, including a follow-up on a session that already exists, and SHALL NOT require a restart to take effect. Tightening the policy SHALL cause a subsequent input from a previously authorized member to be rejected immediately. The change SHALL NOT revoke, interrupt, or delete work that was already accepted before the change, and SHALL NOT delete history.

#### Scenario: Tightening to Owner-only rejects a new follow-up from a previously allowed member

- **WHEN** the policy changes from `allowlist` to `owner_only` and a member who was previously listed sends a new follow-up in a bound thread
- **THEN** the new follow-up is rejected
- **AND** the already-accepted work on that session is not revoked or interrupted

#### Scenario: Loosening to Allowlist accepts a newly listed member

- **WHEN** the policy changes from `owner_only` to `allowlist` and a member newly added to the allowlist sends a channel mention
- **THEN** the invocation is accepted without a restart

#### Scenario: Removing a member from the allowlist rejects the next input only

- **WHEN** a member is removed from the allowlist after their input was already accepted
- **THEN** that already-accepted work continues to completion
- **AND** the next input from that member is rejected

### Requirement: Authorization never auto-succeeds by name

When a member leaves the workspace, is deactivated, or otherwise loses valid regular-member status, that member SHALL no longer be treated as authorized. The system SHALL NOT grant authorization to, or continue a session as, a different member who merely shares a display name or appears in message text.

#### Scenario: A listed member who leaves the workspace is no longer authorized

- **WHEN** a member whose stable identity is in the allowlist has left or been deactivated in the workspace
- **THEN** that member's subsequent invocation is rejected

#### Scenario: A different member with the same display name does not inherit authorization

- **WHEN** a member who shares the display name of a previously authorized member but has a different stable identity invokes the Agent
- **THEN** that member is evaluated solely on their own stable identity and is not authorized by the name match
