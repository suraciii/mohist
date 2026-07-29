### Requirement: Owner transfer generates a fresh single-use claim for an already-owned Connection

An operator SHALL be able to initiate an owner transfer on a Connection that already has an established Owner. Transfer SHALL generate a new short-lived, single-use claim code and SHALL immediately invalidate any previously generated transfer code. The existing Owner SHALL remain the effective Owner until the new claim is successfully redeemed.

#### Scenario: Transfer initiated on an owned Connection
- **WHEN** an operator initiates a transfer on a Connection that has a completed setup and an established Owner
- **THEN** Mohist generates a fresh short-lived single-use claim code and the existing Owner continues to be the effective Owner

#### Scenario: Regeneration invalidates the prior transfer code
- **WHEN** a second transfer code is generated while a prior transfer code is still valid
- **THEN** the prior code is immediately invalid and cannot be used to complete the transfer

### Requirement: The old Owner remains effective until the new Owner claims atomically

Owner transfer SHALL be atomic: the old Owner SHALL retain full Owner privileges until the moment the new Owner successfully redeems the claim code, at which point the Owner binding SHALL swap to the new Owner in a single operation. There SHALL be no window where the Connection has no Owner or two simultaneous effective Owners.

#### Scenario: Old Owner still accepted before new claim
- **WHEN** a transfer code has been generated but not yet redeemed
- **THEN** the old Owner's DM tasks continue to be accepted and dispatched

#### Scenario: Atomic swap on successful claim
- **WHEN** a new eligible member redeems the transfer code in a DM to the Bot
- **THEN** the new member becomes the sole Owner and the prior Owner immediately loses Owner privileges in the same operation

### Requirement: Transfer claim validates workspace regular membership

The new Owner SHALL be a current regular member of the bound workspace at the moment of claim. External collaborators, bots, deactivated members, guests, restricted members, and members of other workspaces MUST NOT be able to complete a transfer. The membership check used for transfer SHALL be the same standard applied to the initial Owner claim.

#### Scenario: Eligible member completes transfer
- **WHEN** a current regular workspace member sends the valid transfer code in a DM to the Bot before it expires
- **THEN** that member becomes the new Owner

#### Scenario: Disqualified identity cannot complete transfer
- **WHEN** a guest, a bot, a deactivated member, or a member of another workspace sends the transfer code
- **THEN** the transfer is rejected, the old Owner remains unchanged, and no swap occurs

### Requirement: Owner departure does not auto-transfer to a same-name member

When the current Owner has left the workspace, been deactivated, or downgraded to a guest or restricted member, Mohist SHALL NOT automatically transfer ownership to another member who happens to share the same display name or user identity. Auto-transfer based on name matching is prohibited; ownership change SHALL require an explicit operator-initiated transfer claim.

#### Scenario: Deactivated Owner is not auto-replaced
- **WHEN** the current Owner has been deactivated or has left the workspace
- **THEN** Mohist does not assign a new Owner automatically, even if another member shares the same display name

#### Scenario: Guest-downgraded Owner is not auto-replaced
- **WHEN** the current Owner has been downgraded to a guest or restricted member
- **THEN** ownership is not automatically transferred and the Connection surfaces the Owner-unavailable state for an operator to act on

### Requirement: Owner unavailability is diagnosable but does not trigger automatic transfer

Owner unavailability (the bound Owner has left, been deactivated, or downgraded below regular membership) SHALL be surfaced as a diagnosable state so an operator knows action is needed, but SHALL NOT by itself initiate a transfer or revoke the bound Owner identity. Resolution SHALL require an explicit operator-initiated transfer.

#### Scenario: Unavailable Owner surfaces a diagnostic
- **WHEN** the bound Owner is no longer a current regular member of the workspace
- **THEN** the Connection surfaces an Owner-unavailable diagnostic with a transfer action as the next step, without removing the bound Owner or auto-assigning a new one

### Requirement: The CLI exposes owner transfer

The CLI SHALL provide `mo agent connection transfer-owner <connection-id>` that generates a fresh transfer claim code and reports the code and its expiry.

#### Scenario: Initiating a transfer from the CLI
- **WHEN** an operator runs `mo agent connection transfer-owner <id>` on an owned Connection
- **THEN** the command outputs the one-time transfer code and its expiry, and instructs the new Owner to send it in a DM to the Bot
