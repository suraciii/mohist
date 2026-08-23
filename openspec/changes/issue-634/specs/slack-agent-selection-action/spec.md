### Requirement: Every choice carries a Server-signed selection payload

Every candidate choice on the chooser SHALL carry a Server-signed action payload that binds the posting Connection, the workspace, conversation, and message identity of the chooser, the original sender, the full candidate set, the chosen candidate, a fresh nonce, and a bounded expiry fixed when the chooser is rendered. The payload SHALL use the same signing material (the Connection's bot token), canonical field ordering, and constant-time HMAC verification the Stop and Retry actions use. A payload that fails structural validation or whose signature does not verify SHALL be rejected as an invalid action.

#### Scenario: A tampered choice value is rejected

- **WHEN** a click submits a selection payload whose fields were modified after signing
- **THEN** signature verification fails and the selection is rejected as invalid
- **AND** no execution resources are created

#### Scenario: A payload signed with another Connection's key fails verification

- **WHEN** a selection payload is verified against the posting Connection but was signed with a different Connection's signing key
- **THEN** the constant-time comparison fails and the selection is rejected as invalid

### Requirement: Acceptance revalidates freshness, context, and actor binding

On every click the Server SHALL revalidate the selection payload before any work starts: the expiry SHALL not have passed; the interaction's workspace, conversation, and chooser message identity SHALL match the payload; the interaction SHALL be delivered to the posting Connection; and the clicking member SHALL match the actor bound in the payload. An expired selection SHALL be rejected as expired, a context or Connection mismatch as stale, and an actor mismatch as unauthorized.

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

### Requirement: The clicker's permission is re-evaluated under the chosen Connection's current access policy

Acceptance SHALL evaluate the clicker's current permission under the chosen candidate Connection's access policy using the existing Slack access decision path at click time, rather than trusting authorization state captured when the chooser was rendered. The evaluation SHALL include the lease context of the delivering adapter. A clicker not currently allowed SHALL be rejected as unauthorized with that policy's actionable reason, and no execution resources SHALL be created.

#### Scenario: Access narrowed between render and click

- **WHEN** the chosen Connection's access policy changes so the clicker is no longer allowed after the chooser was rendered
- **THEN** the click is rejected as unauthorized with the policy's actionable reason
- **AND** no execution resources are created

#### Scenario: An allowed clicker passes re-evaluation

- **WHEN** the clicker remains allowed under the chosen Connection's current access policy
- **THEN** the permission re-evaluation passes and selection processing continues

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

Expired, tampered, stale, unauthorized, and no-longer-valid selections SHALL be explicitly rejected with distinct, user-visible notices delivered through the existing interaction reply path that updates the chooser message. Every rejection outcome SHALL create no AgentJob, AgentSession, SessionInput, selection execution record, or provider inbox entry.

#### Scenario: Each rejection kind surfaces a distinct visible notice

- **WHEN** a click is rejected as expired, invalid, stale, unauthorized, or no longer valid
- **THEN** the chooser message is updated with a notice naming that rejection outcome
- **AND** no AgentJob, AgentSession, SessionInput, or provider inbox entry is created

### Requirement: The selection action coexists with Stop and Retry on the shared interaction route

The selection action id SHALL be dispatched from the existing Slack interaction route alongside the Stop and Retry action ids, reusing unchanged the adapter operator authentication, runtime lease validation, disabled-Connection check, and outbox user-action reply delivery. The Slack adapter's `block_actions` forwarding SHALL remain generic over action ids and blocks, adding no adapter contract change beyond covering the new action id in adapter tests. The chooser button SHALL be a shortcut to the same launch and admission services the CLI and Web use, never a second command grammar.

#### Scenario: The selection action id routes to selection handling

- **WHEN** the interaction route receives a `block_actions` interaction carrying the selection action id with a valid adapter lease
- **THEN** it is dispatched to selection handling with the same authentication and lease validation the Stop and Retry actions use

#### Scenario: A stale lease rejects the selection before any work

- **WHEN** a selection interaction arrives with a stale, expired, or unknown runtime Socket lease
- **THEN** the interaction is rejected with the existing lease-stale outcome
- **AND** no selection processing or execution resources result

#### Scenario: Stop and Retry remain unaffected

- **WHEN** a Stop or Retry interaction arrives after the selection action is added to the shared route
- **THEN** each is still dispatched to its existing handling with unchanged outcomes
