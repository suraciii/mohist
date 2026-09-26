# Connections

Configure access and lifecycle for an Agent Connection after
[enrollment](../enrollment/spec.md). [Interaction](../interaction/spec.md)
and [delivery](../delivery/spec.md) consume that binding.

## Agent Connection Configuration

A Connection carries these settings:

- **Agent:** fixed at creation. Create another Connection for another Agent.
- **Slack Workspace:** confirmed by Mohist App installation, never a
  user-entered name.
- **Bot identity:** initialized from the Agent name and avatar, then managed in
  Slack and verified by Mohist.
- **Slack description:** initialized from Agent Description. It never becomes
  Instructions or a hidden prompt.
- **Runtime mode:** local Socket Mode with one Bot token and App-level token
  per App.
- **Owner:** a claimed Slack member. The Owner is the only caller by default
  and is always in the Allowlist.
- **Access policy:** who may invoke in channels. Owner only is the default.
- **Allowed members:** Workspace members who may invoke under Allowlist. DMs
  remain Owner-only.

A Connection reports independent installation progress, status, and identity
sync. `Degraded` carries one actionable reason. Identity drift never presents
as disconnection. If the Agent name or avatar changes, Mohist reports the
expected Slack values and the user updates Slack. A Bot token cannot read the
full App configuration, so Mohist reports a gap only when a capability fails.

Instructions, Runtime, Model, Variant, Skills, and concurrency limit belong to
the Agent. New work uses the new execution snapshot. A concurrency-limit
change applies to later launches and follow-ups, not running input.

## Permissions

Three access policies decide who may invoke a Bot:

- **Owner only** (default): only the Owner may invoke in DMs and channels.
- **Allowlist:** DMs remain Owner-only; listed members may invoke in channels
  and bound threads.
- **Anyone:** DMs remain Owner-only; any verified full Workspace member may
  invoke in a channel where the Bot is present.

Anyone who can invoke the Bot can use every capability granted to the Agent.
Widening a policy is a permission grant. DMs are Owner-only under every policy.
Allowlist and Anyone reverify that the sender is a full Workspace member on
every invocation. Deactivated, restricted, external, Bot, and unconfirmed
identities are rejected. Anyone also verifies Bot channel membership.

Channel membership does not replace access policy. Slack Connect participants
and identities whose ownership cannot be confirmed cannot invoke in the first
version. An unauthorized user gets an explicit rejection and Mohist creates no
AgentJob or AgentSession.

Only the Connection Owner or Session starter can stop its Turn. A stale button
cannot stop a later Turn.

Access policy does not alter Agent capability. A Slack message cannot add or
replace capability, switch Project or Agent, or change policy. Only the Owner
can change invocation scope. A policy change applies to later inputs, including
follow-ups; it does not revoke accepted work or delete history. Only a Mohist
operator can start Owner transfer. The old Owner remains until a current full
Workspace member claims through a Bot DM.

## Lifecycle and Failures

- **Disable** pauses new Slack input and replies while accepted execution
  continues. The App and management facts remain. Enable restores delivery of
  current or final state, never stale progress from the disabled interval.
- **Remove binding** detaches the Connection and clears receipts, Session
  mappings, and pending delivery. It preserves Agent App management facts and
  does not uninstall the Slack App.
- **Permanent delete** deletes only a Mohist-created Slack App. It requires
  separate permission, explicit confirmation, complete audit, and no active
  binding. An unknown delete result is reconciled or arbitrated, never claimed
  as success.
- Agent edits never change running work. New AgentJobs use the new snapshot.
  Existing Sessions keep theirs. A description edit updates the expected Slack
  description, not Agent behavior.
- An archived Agent rejects new root delegations while existing Sessions remain
  readable and continuable. An Agent that needs setup gives a safe summary; an
  unknown Readiness waits for Runner validation and fails explicitly if it is
  unusable.
- Installation and recovery never create duplicates. An invalid credential
  makes the Connection Degraded and stops new input until the same identities
  are revalidated. A temporary Socket outage preserves the Connection.
- Owner loss makes the Connection Degraded with reason **Owner unavailable**;
  it never transfers ownership silently. Channel Allowlist and Anyone policies
  continue. DMs and Owner management wait for transfer.
- Delivery uncertainty settles to the same input record, never a replayed input.
- Capacity is not execution failure. Full concurrency or Session queues show
  work as queued or reject it with retry-later.
- Stopping a queued Turn ends it as cancelled. Stopping its first Turn ends the
  AgentJob with failure category `cancelled`.
