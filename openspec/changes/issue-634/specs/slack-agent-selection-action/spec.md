### Requirement: Every choice carries a Server-signed selection payload

Every candidate choice on the chooser SHALL carry a Server-signed action payload that binds the posting Project and Connection, the workspace, conversation, and message identity of the chooser, the original sender, the full ordered candidate set as `(ProjectId, ConnectionId)` references, the chosen Project/Connection reference, a fresh nonce, and a bounded expiry fixed when the chooser is rendered — the same five-minute signed-action lifetime the Stop and Retry actions use, per the issue's pinned parameter. The payload SHALL use the same signing material (the posting Connection's bot token), unambiguous canonical field ordering, and constant-time HMAC verification the Stop and Retry actions use. A payload that fails structural validation or whose signature does not verify SHALL be rejected as an invalid action.

#### Scenario: A tampered choice value is rejected

- **WHEN** a click submits a selection payload whose fields were modified after signing
- **THEN** signature verification fails and the selection is rejected as invalid
- **AND** no execution resources are created

#### Scenario: A payload signed with another Connection's key fails verification

- **WHEN** a selection payload is verified against the posting Connection but was signed with a different Connection's signing key
- **THEN** the constant-time comparison fails and the selection is rejected as invalid

### Requirement: Acceptance revalidates freshness, context, and actor binding

On every click the Server SHALL revalidate the selection payload before any work starts: the expiry SHALL not have passed; the interaction's workspace, conversation, and chooser message identity SHALL match the payload; the interaction SHALL be delivered to the posting Project/Connection pair; the ordered candidate references SHALL exactly match the durable claim snapshot; and the clicking member SHALL match the actor bound in the payload. An expired selection SHALL be rejected as expired, a context, Project/Connection, or candidate-snapshot mismatch as stale, and an actor mismatch as unauthorized.

#### Scenario: An expired choice is rejected

- **WHEN** a user clicks a chooser choice after its bounded expiry has passed
- **THEN** the selection is rejected as expired with a visible notice
- **AND** no execution resources are created

#### Scenario: A choice replayed outside its context is rejected

- **WHEN** a selection payload is submitted with a workspace, conversation, or chooser message identity that differs from the interaction envelope
- **THEN** the selection is rejected as stale
- **AND** no execution resources are created

#### Scenario: A different member cannot click another member's choice

- **WHEN** a Slack member other than the actor bound in the payload clicks the choice
- **THEN** the selection is rejected as unauthorized
- **AND** no execution resources are created

### Requirement: Candidate identity preserves the selected Connection's owning Project

Acceptance SHALL treat a candidate as the complete `(ProjectId, ConnectionId)` reference stored in the claim and signed payload. It SHALL resolve the chosen Connection through the normal project-scoped lookup using `ChosenProjectId` and `ChosenConnectionId`; it SHALL NOT perform a global lookup by Connection id or substitute the posting Connection's Project. The candidate SHALL remain in the exact durable candidate snapshot and bound to the chooser workspace, otherwise the click SHALL be rejected as no longer valid with no selection mutation or execution resources.

#### Scenario: A candidate in another Project resolves by its owning Project

- **WHEN** a chooser posted by Project A contains a signed and snapshotted candidate `(ProjectB, ConnectionB)`
- **THEN** acceptance resolves Connection B with Project B and continues to Project B's lease and policy checks
- **AND** it does not attempt to resolve Connection B under Project A

#### Scenario: A selected Project id cannot be replaced independently

- **WHEN** a click value changes `ChosenProjectId` while retaining `ChosenConnectionId`, or names a pair absent from the durable ordered candidate snapshot
- **THEN** signature or context/candidate validation rejects the click
- **AND** no selection mutation or execution resource is created

### Requirement: The prompt-owner Connection is re-authorized under its current access policy and own current lease

Before candidate selection can commit, acceptance SHALL evaluate the clicker's current permission under the posting (prompt-owner) Connection's access policy using `SlackConnectionAccessDecider` and the prompt-owner's own currently route-validated runtime lease context. This evaluation SHALL re-read current policy and allowlist state and, where the policy requires it, re-prove current owner/live-member/channel-membership authorization; render-time authorization and actor binding alone SHALL NOT substitute for it. A prompt-owner denial SHALL be rejected as unauthorized with that policy's actionable reason and SHALL create no selection mutation, winner, provider inbox entry, AgentSession, Turn, or AgentJob. When the prompt-owner and chosen Project/Connection pairs are the same, one equivalent current evaluation under that Connection's current lease MAY satisfy both authorization roles.

#### Scenario: The prompt-owner policy narrows between render and click

- **WHEN** the posting Connection's access policy changes after render so the bound actor is no longer allowed, while the chosen Connection remains valid and would allow the actor
- **THEN** the click is rejected as unauthorized with the prompt-owner policy's actionable reason
- **AND** no selection mutation, winner, or execution resource is created

#### Scenario: The actor is removed from the prompt-owner allowlist between render and click

- **WHEN** the posting Connection uses allowlist access and the bound actor is removed from its allowlist before clicking
- **THEN** prompt-owner re-authorization rejects the click as unauthorized
- **AND** the chosen Connection is not committed even if its own current policy would allow the actor

#### Scenario: The prompt owner can no longer verify the actor as a live member

- **WHEN** the posting Connection's current policy requires live-member verification and the actor is deleted, restricted, external, or cannot be confirmed at click time
- **THEN** prompt-owner re-authorization rejects the click as unauthorized with the current access decision reason
- **AND** no selection mutation or execution resource is created

#### Scenario: The prompt-owner Bot no longer has the required channel membership

- **WHEN** the posting Connection's current policy requires channel membership and its Bot is no longer a member of, or cannot verify, the chooser conversation at click time
- **THEN** prompt-owner re-authorization rejects the click as unauthorized with the current access decision reason
- **AND** no selection mutation or execution resource is created

#### Scenario: The same Project/Connection pair may satisfy both authorization roles with one current evaluation

- **WHEN** the posting Project/Connection pair is also the chosen pair and its current lease and access decision allow the actor
- **THEN** one equivalent current lease-and-policy evaluation may satisfy both prompt-owner and selected-Connection authorization
- **AND** selection processing continues without weakening either authorization requirement

### Requirement: The clicker's permission is re-evaluated under the chosen Connection's current access policy and own current lease

Acceptance SHALL separately evaluate the clicker's current permission under the chosen candidate Connection's access policy using the existing Slack access decision path at click time, rather than trusting authorization state captured when the chooser was rendered. The evaluation SHALL run under the chosen Connection's own currently active runtime lease, resolved from the Server's lease authority at click time; the delivering (prompt-owner) adapter's lease SHALL NOT be used for the chosen Connection's evaluation. A clicker not currently allowed SHALL be rejected as unauthorized with that policy's actionable reason, and no selection mutation or execution resources SHALL be created. The only permitted de-duplication is the identical Project/Connection-pair case defined by the prompt-owner authorization requirement.

#### Scenario: Access narrowed between render and click

- **WHEN** the chosen Connection's access policy changes so the clicker is no longer allowed after the chooser was rendered
- **THEN** the click is rejected as unauthorized with the policy's actionable reason
- **AND** no execution resources are created

#### Scenario: An allowed clicker passes re-evaluation

- **WHEN** the clicker remains allowed under the chosen Connection's current access policy
- **THEN** the permission re-evaluation passes and selection processing continues

#### Scenario: A cross-Project selection by an allowed clicker is authorized under the chosen Connection's own lease

- **WHEN** the clicker selects a candidate in Project B while the prompt owner is in Project A, remains allowed under both current access policies, and the chosen Connection holds a valid current runtime lease for `connection:ProjectB:ChosenConnection`
- **THEN** the chosen Connection is resolved in Project B and the permission evaluation passes under its own lease and policy
- **AND** the selection is not rejected merely because the interaction arrived through Project A or because the delivering Connection's lease does not validate against the chosen target

#### Scenario: A cross-Project selection is rejected when the chosen Connection's policy denies the clicker

- **WHEN** the prompt owner is in Project A, the selected Connection is in Project B, and Project B's chosen Connection current access policy does not allow the clicker
- **THEN** the selection is rejected as unauthorized with the chosen Connection's policy reason
- **AND** no selection mutation or execution resource is created in either Project

### Requirement: The chosen Connection's current runtime lease is resolved at click time

Acceptance SHALL resolve the chosen candidate through its signed and snapshotted owning `ProjectId` plus `ConnectionId`, then resolve that Connection's own current runtime lease at click time under target `connection:{ChosenProjectId}:{ChosenConnectionId}`. It SHALL reject the selection as unavailable when the chosen Connection holds no currently valid runtime lease — absent, expired, superseded, or invalidated by a target or credential-generation change. The prompt-owner Project or Connection lease SHALL NOT be accepted as a substitute, and no execution resources SHALL be created for an unavailable selection.

#### Scenario: The chosen Connection has no current runtime lease

- **WHEN** a choice is clicked whose chosen Connection no longer holds a valid current runtime lease
- **THEN** the selection is rejected as unavailable with a visible notice
- **AND** no execution resources are created

#### Scenario: The prompt-owner Project and lease do not substitute for the chosen target

- **WHEN** a Project A prompt receives a choice for a Project B candidate while the posting Connection's lease is valid but the Project B Connection's own lease is missing or expired
- **THEN** the selection is rejected as unavailable after checking `connection:ProjectB:ChosenConnection`, not `connection:ProjectA:ChosenConnection`
- **AND** no selection mutation or execution resources are created

### Requirement: The chosen candidate's executability is revalidated before work starts

At click time the Server SHALL revalidate that the chosen Connection is enabled and the chosen Agent is currently executable, using the existing admission and setup-nudge path. When the Agent is not ready or the Connection is unavailable, the Server SHALL post the existing setup-nudge guidance through its durable once-only delivery and create no execution resources. A disabled chosen Connection SHALL be rejected with the existing connection-disabled outcome.

#### Scenario: The Agent is not executable at click time

- **WHEN** the chosen Agent is not configured or not executable when the choice is clicked
- **THEN** the existing Agent-not-ready setup nudge is posted durably for the triggering interaction
- **AND** no AgentJob, AgentSession, or SessionInput is created

#### Scenario: The chosen Connection is disabled at click time

- **WHEN** the chosen candidate Connection is disabled when the choice is clicked
- **THEN** the selection is rejected with the existing connection-disabled outcome
- **AND** no execution resources are created

### Requirement: Rejections are visible and create no execution resources

Expired, tampered, stale, unauthorized, unavailable, and no-longer-valid selections SHALL be explicitly rejected with distinct, user-visible notices delivered through the existing interaction reply path that updates the chooser message. Every rejection outcome — including a current-policy denial by either the prompt-owner or chosen Connection — SHALL create no selection mutation, winner, AgentJob, AgentSession, SessionInput, Turn, or provider inbox entry.

#### Scenario: Each rejection kind surfaces a distinct visible notice

- **WHEN** a click is rejected as expired, invalid, stale, unauthorized, unavailable, or no longer valid
- **THEN** the chooser message is updated with a notice naming that rejection outcome
- **AND** no AgentJob, AgentSession, SessionInput, or provider inbox entry is created

### Requirement: The selection action coexists with Stop and Retry on the shared interaction route

The selection action id SHALL be dispatched from the existing Slack interaction route alongside the Stop and Retry action ids, reusing unchanged the adapter operator authentication, runtime lease validation, disabled-Connection check, and outbox user-action reply delivery. The route SHALL pass the prompt-owner's route-validated lease context to selection handling for current prompt-owner access evaluation; selection handling SHALL separately resolve the chosen Connection's own lease and SHALL NOT reuse the prompt-owner lease for that chosen-Connection evaluation. The Slack adapter's `block_actions` forwarding SHALL remain generic over action ids and blocks, adding no adapter contract change beyond covering the new action id in adapter tests. The chooser button SHALL be a shortcut to the same launch and admission services the CLI and Web use, never a second command grammar.

#### Scenario: The selection action id routes to selection handling

- **WHEN** the interaction route receives a `block_actions` interaction carrying the selection action id with a valid adapter lease
- **THEN** it is dispatched to selection handling with the same authentication and lease validation the Stop and Retry actions use
- **AND** the route-validated prompt-owner lease context is available for prompt-owner current-policy re-authorization

#### Scenario: A stale lease rejects the selection before any work

- **WHEN** a selection interaction arrives with a stale, expired, or unknown runtime Socket lease
- **THEN** the interaction is rejected with the existing lease-stale outcome
- **AND** no selection processing or execution resources result

#### Scenario: Stop and Retry remain unaffected

- **WHEN** a Stop or Retry interaction arrives after the selection action is added to the shared route
- **THEN** each is still dispatched to its existing handling with unchanged outcomes
