## Context

Issue 526 widens Slack Agent channel invocation from hard-coded Owner-only to three access
policies, and adds channel-side stop authority. The product spec is `docs/agent-connections.md:313-347`;
the binding architecture is `design/slack-agent-connection.md` (访问控制 + 安全边界). Prerequisite
515 (channel/thread routing) is done and deliberately left the entire policy surface to this issue
(515 design D7 + Non-Goals).

Current state (sourced from code):

- **Channel access is Owner-only via 6 inlined equality checks.** `sender == connection.OwnerSlackUserId`
  is duplicated at `SlackConnectionRoutes.cs:1186,1208,1286,1308,1325` (channel), plus the DM gate at
  `SlackOwnerClaimService.cs:148-150`. Each rejected site declares its own copy of the reason string
  `"This Slack Connection is available only to its owner."`. There is no `AccessPolicy` field, no
  allowlist storage, and no access-decision abstraction.
- **The ingress already carries everything a decision needs.** `HandleChannelIngressRequest`
  (`SlackConnectionRoutes.cs:1790-1844`) bundles `Connection`, `SenderSlackUserId`, `Body` (with
  `TeamId`, `ConversationId`, `Text`, `MentionedUserIds`, `ThreadTs`), `Secrets`, `Sessions`,
  `Grains`. `SlackIngressBody.Text` (`:1879`) is present for keyword detection.
- **A reusable "regular workspace member" predicate exists.** `SlackOwnerClaimService.IsEligibleMember`
  (`SlackOwnerClaimService.cs:234-244`) — rejects bot/deleted/guest/restricted/other-team. `ISlackApiClient`
  (`ISlackApiClient.cs:7-22`) offers `UsersInfoAsync` (member status) and `ConversationsInfoAsync`
  (returns `SlackConversationInfo.IsMember` — whether the Bot is in the channel).
- **A cascade child-store pattern is established.** `IAgentConnectionProviderCleanup`
  (`IAgentConnectionProviderCleanup.cs:12`) is invoked by `AgentConnectionStore.DeleteProviderRecordsAsync`
  (`:257`) over `IEnumerable<IAgentConnectionProviderCleanup>`. Each store implements it and is
  forward-registered in `MohistServiceRegistration.cs:98-111` (6 stores today).
- **Connection editing is field-scoped.** `AgentConnectionStore.UpdateAsync` (`:153-198`) takes a
  `fields` set and an immutable-binding guard (`:12-20`); `PATCH /{id}` (`SlackConnectionRoutes.cs:153`)
  builds that set from `botName`/`avatarHash` only. Management routes are project-resolved
  (`ProjectResolutionEndpointFilter`) with no adapter credential.
- **Session initiator provenance is recorded but not yet consumed for authority.** Each accepted input
  stamps `AgentSessionInputProvenance.MemberId` (`AgentSession.cs:533-540`). `IAgentSessionGrain.GetInitialLaunchAsync()`
  (`IAgentSessionGrain.cs:104`) returns the first input; `AgentSessionQuerier.ResolveCancelTargetAsync`
  (`:370`) resolves a session for control. DM stop/cancel parses "stop"/"cancel" keywords
  (`SlackConnectionRoutes.cs:756-775`) and dispatches via `ExecuteTurnControlAsync` (`:816`). There is
  no channel-side stop surface (515 Non-Goal).

The repo is in active development with no version-compatibility constraint (`AGENTS.md`); additive
storage and bounded internal changes are acceptable.

## Goals / Non-Goals

**Goals:**

- Let the Owner select `owner_only` (default), `allowlist`, or `anyone` and manage the allowlist by
  stable Slack identity, with the Anyone execution-grant disclosed before it takes effect.
- Consolidate the 6 inlined Owner-only checks into one access-decision point that reads current state
  per invocation, so policy/allowlist changes take effect on the next input without a cache to invalidate.
- Authorize under `allowlist` (Owner + listed current regular members) and `anyone` (current regular
  workspace members where the Bot is in the channel), while DM stays Owner-only under every policy.
- Reject unauthorized invocation with an actionable reason and no created AgentJob/Session/Input/inbox row.
- Add channel-side stop/cancel, permitted only to the Owner or the session's Slack initiator.

**Non-Goals:**

- Reducing or expanding the Agent's own Runtime/Skills/repository/tool authority via the policy.
- Slack Connect external members, group DM, or cross-Mohist-Server multi-Bot coordination.
- Per-channel, time-window, or task-type fine-grained authorization.
- Treating Slack member identity as Mohist administrator identity.
- A Slack-native (slash-command/in-app) Manage-access surface; v1 manages access via the Mohist Web/CLI
  control plane. Adding a Slack-owner bearer on management routes is a follow-up (see Open Questions).
- Background reconciliation/reaping of stale allowlist members; membership is validated live at invocation.

## Decisions

### D1. Policy is a parent-row scalar; the allowlist is a cascade child table

`AgentConnection` gains an `AccessPolicy` column (default `owner_only`) with kind constants in a new
`AccessPolicyKind` (mirroring `SetupProgressKind`/`DesiredStateKind`). The allowed members live in a
new child store `SlackConnectionAllowedMemberStore` (`Infrastructure/Slack/`) with row
`Infrastructure/Data/Slack/SlackConnectionAllowedMemberRow.cs`, uniquely keyed on
`(ProjectId, ConnectionId, SlackUserId)` → denormalized `WorkspaceTeamId`. It implements
`IScopedService` + `IAgentConnectionProviderCleanup` (an `ExecuteDeleteAsync` filtered by
`(projectId, connectionId)`, mirroring `SlackThreadSessionMappingStore.cs:194-205`) and is added as the
**7th** forward-registration in `MohistServiceRegistration.cs:98-111`. A `DbSet` + EF migration is added
to `MohistDbContext`.

The Owner is **never** stored in the child table — owner authority is implicit and unconditional. This
makes "Owner always in the allowlist, cannot be removed" a structural invariant rather than a check.

`AccessPolicy` is threaded through `UpdateAsync` (`:153`) as a non-immutable field (add a parameter, a
`fields.Contains(nameof(...))` line, and `ToRow`/`ToDomain` mapping); it is **not** added to
`ImmutableBindingFields` (`:12-20`).

**Rationale:** the policy is single-valued per Connection, so it belongs on the parent row beside
`OwnerSlackUserId`/`DesiredState`. The allowlist is multi-row and must cascade on delete, which is
exactly the established child-store shape. Keeping the Owner out of the child table removes a whole
class of "Owner accidentally removed" bugs.

**Alternatives:** (a) Store the allowlist as a JSON column on the parent — rejected: loses per-row
uniqueness/indexing and the cascade story; diverges from the established per-Connection child-store
pattern. (b) One combined table holding policy + members — rejected: policy is single-valued;
splitting keeps the parent row the single source of the mode. (c) An Orleans grain for the allowlist —
rejected for v1: writes are rare (Manage access) and reads are one-per-invocation behind inbox dedup;
a relational row suffices without activation machinery (same reasoning as 515 D2).

### D2. One access-decision service replaces the 6 inlined checks; decisions are never cached

Add `SlackConnectionAccessDecider` (`Agent/Services/`, `IScopedService`) exposing
`EvaluateAsync(connection, senderSlackUserId, workspaceTeamId, conversationId, isDirectMessage, ct) →
AccessDecision(Allowed, Reason)`. It replaces sites A–E in `SlackConnectionRoutes` and the DM gate.
The decision reads the **current** `AccessPolicy` column and (for allowlist) the **current** child-table
rows on every call; it caches nothing. The `HandleChannelIngressRequest` record already carries
`Connection`, `SenderSlackUserId`, `Body.TeamId`/`ConversationId`, and `Secrets`, so the decider needs
no new ingress plumbing; the DM path passes `isDirectMessage: true`.

Decision rules:

- **DM (`isDirectMessage`)** → allow iff `sender == OwnerSlackUserId`, regardless of policy. No Slack API
  call; the decider short-circuits before any member lookup.
- **`owner_only`** → allow iff `sender == OwnerSlackUserId`. No Slack API call (the default path stays
  zero-extra-call, preserving today's latency).
- **`allowlist`** → allow iff Owner, or `sender` is a row in the child table **and** is currently a valid
  regular member of the workspace (D3).
- **`anyone`** → allow iff sender is currently a valid regular workspace member **and** the Bot is a
  member of the channel (D3).

Because there is no cache, "tightening rejects the next input immediately" and "loosening accepts the
next input without restart" are automatic consequences, and already-accepted work is never touched (the
decider gates only *new* inputs).

**Rationale:** the 6 duplicated checks are the single largest maintenance hazard in this area; one
decision point makes the policy rules testable in isolation and guarantees every ingress path applies
the same rule. No caching is the simplest correct way to meet the immediate-effect requirement, and the
read cost is one column + (allowlist) one small indexed query per invocation — acceptable behind inbox
dedup.

**Alternatives:** (a) Cache the decision per-Connection with invalidation on Manage access — rejected:
adds a coherence problem (a cache miss during invalidation could admit a just-revoked member) for no
measurable gain at current ingress volume. (b) Leave the checks inlined and add policy branches at each
site — rejected: 6× the places to get wrong, and the non-owner rejection path already diverges across
sites. (c) Put the decision on a grain — rejected: the inbox dedup is the serialization boundary and the
decision is a read + branch, not stateful (515 D3 reasoning).

### D3. Allowlist/Anyone prove current member status via `users.info`; Anyone also proves Bot channel membership via `conversations.info`

For a non-Owner sender under `allowlist` or `anyone`, the decider loads the Connection's Bot token
(`ISecretStore.LoadAsync(SecretKind.BotToken)`, as `SlackOwnerClaimService.cs:172` does) and calls
`UsersInfoAsync(senderSlackUserId, botToken)` then `SlackOwnerClaimService.IsEligibleMember(response,
workspaceTeamId, senderSlackUserId)`. Under `anyone` it additionally calls
`ConversationsInfoAsync(conversationId, botToken)` and requires `Channel.IsMember == true`. A listed
member who has since become a guest/deleted/restricted, or a sender in a channel the Bot is not in, is
rejected.

**Safe degradation:** if a Slack API call returns not-OK, throws, or cannot confirm the fact, the
decision is **deny** — identities that cannot be confirmed never trigger the Agent (matches the Anyone
spec and the Buzz DM-hardening principle). This favors safety over availability; the trade-off is noted
below.

**Rationale:** the spec requires invocation-time validity ("a listed member who has become a guest is
rejected") and Anyone's "can see the Bot in the channel" proof, which are only knowable live from Slack.
Reusing `IsEligibleMember` keeps "regular workspace member" semantics identical across claim, diagnostic,
and access paths. The `owner_only` default pays none of this cost, so only connections that opt into
wider policies take the extra calls.

**Alternatives:** (a) Validate members only at add time and trust the list at invocation — rejected:
fails the "member downgraded between add and invoke" scenario. (b) Anyone without the `conversations.info`
check (workspace membership alone) — rejected: the spec requires channel visibility of the Bot, which
also guards against shared-channel/Slack-Connect leak. (c) Allow on API failure (availability over
safety) — rejected: an unverifiable identity triggering the Agent's configured write/tools is the worse
failure direction.

### D4. Manage access is a dedicated `POST /{id}/manage-access` endpoint that replaces the whole allowlist and validates members before mutation

Add `POST /api/projects/{projectRef}/slack-connections/{id}/manage-access` (project-resolved, no adapter
credential, matching every other management route). Body: `{ accessPolicy: "owner_only"|"allowlist"|"anyone",
allowMembers: string[] }`. Semantics:

- If `accessPolicy` is `owner_only` or `anyone` and `allowMembers` is non-empty → `400` before any
  mutation (the `--allow-member` incompatibility rule).
- For each member in `allowMembers`, load the Bot token and call `UsersInfoAsync` + `IsEligibleMember`;
  reject the whole request (`400`) if any is a bot/guest/deleted/restricted or belongs to another
  workspace. Members are resolved/validated by stable id; the Web/CLI may present name/avatar for
  selection, but the submitted identity is the stable id.
- Persist atomically: update the parent `AccessPolicy` column and replace the child-table rows (delete
  existing, insert the new set) in one `DbContext` transaction. The Owner is never inserted.
- Return the updated connection read model.

The Anyone execution-grant disclosure (`docs/agent-connections.md:321-323`) is returned as a contract
field on the connection read model (e.g. `anyoneDisclosure`); the Web and CLI layers render it and
require the Owner to proceed before calling the endpoint with `anyone`. The server does not block on a
separate confirmation round-trip; the disclosure is a documented client-enforced contract (see Risks).

**Rationale:** a replace is a multi-field, validate-then-persist mutation that does not fit the
field-scoped `PATCH /{id}`. Validating every member before any write makes the operation all-or-nothing,
so a bad member id cannot leave the allowlist half-replaced. A dedicated endpoint keeps the PATCH
surface (presentation fields) and the access surface (security-relevant) cleanly separated.

**Alternatives:** (a) Overload `PATCH /{id}` with `accessPolicy`/`allowMembers` — rejected: PATCH is
field-level and cannot express "replace the whole list atomically"; mixing also blurs the
security-relevant surface. (b) Add/remove one member per call (delta API) — rejected: the CLI contract
is whole-list replace (`docs/agent-connections.md:145`), and deltas complicate the Owner-immovable and
incompatibility rules. (c) Server-side confirmation token for Anyone — rejected for v1: adds a second
round-trip and state; the disclosure is effectively a warning, and the client layer can enforce it.

### D5. Channel stop/cancel is a keyword in the channel ingress, authorized by Owner or the session initiator

Extend the channel state machine (515 D3) with a control branch. After a channel message is resolved to
this Connection (single-mention target, or a reply in a thread bound to this Connection) and **before**
launch/follow-up, inspect `Body.Text` with the existing `TryGetTurnControlCommand`
(`SlackConnectionRoutes.cs:756`) applied to the text after stripping the Bot mention. If it yields
`stop`/`cancel`:

1. Resolve the thread's bound session via `SlackThreadSessionMappingStore` (the launch reservation /
   binding from 515 D2).
2. Authorize: the sender is the Owner, **or** the sender equals the session's initiator. The initiator
   is `GetInitialLaunchAsync()` (`IAgentSessionGrain.cs:104`) → `Input.Provenance.MemberId`. Compare by
   stable id.
3. If unauthorized → reject with an actionable reason, create no resources, do not interrupt the Turn.
4. If authorized and a current Turn exists → reuse `ResolveCurrentTurnControlAsync` +
   `ExecuteTurnControlAsync` (`SlackConnectionRoutes.cs:816`); if no active Turn → reply "no active work"
   / "already ended" exactly as the DM path does.

A stop/cancel targets only the Turn active when the request is processed; a gesture for an already-ended
Turn does not stop a later Turn (the existing `ResolveCurrentTurnControlAsync` null-check already enforces
this). An unauthorized member may still send a normal follow-up (the control branch is only taken when
the stripped text is a standalone keyword).

**Rationale:** the DM path already parses "stop"/"cancel" and dispatches via `ExecuteTurnControlAsync`;
the channel path reuses both. The initiator is already recorded per input, so authority is a stable-id
compare with no new provenance storage. Reusing `ExecuteTurnControlAsync` inherits the existing
cancel-vs-stop semantics and result kinds.

**Alternatives:** (a) A dedicated slash command (`/mo stop`) — rejected for v1: requires a new Slack
command surface and registration; a keyword in-thread is the lowest-friction channel control and matches
the DM interaction model. (b) Resolve the initiator from a session label instead of `GetInitialLaunchAsync`
  — rejected: the `MemberId` provenance lives on the input record, not the session labels; the grain is
  the canonical source and the call is one-per-stop. (c) Let any authorized member stop any Turn —
  rejected: violates the spec ("cannot stop another member's Turn").

### D6. CLI and Web surfaces

**CLI:** `connection edit` (`MohistCliCommands.AgentConnection.cs:339`) gains `--access-policy` and a
repeatable `--allow-member`. When either is present, the CLI calls `POST /{id}/manage-access` (not the
PATCH); `--bot-name`/`--avatar-hash` may still be combined and trigger the PATCH separately. The CLI
renders the `anyoneDisclosure` and requires confirmation before sending `anyone`.

**Web:** the Connection panel (`design/web-ui.md:81-83`) renders the current policy, an allowlist editor
backed by member name/avatar search that resolves to stable ids, and the Anyone disclosure. Display names
are presentation only — the persisted identity is the stable id (D4).

**Rationale:** the CLI flags match the documented command shape (`docs/agent-connections.md:135`); routing
them to the dedicated endpoint preserves the replace + validation contract. Web member search is the
human-facing control while the authorization identity remains the stable id, consistent with the
"never authorize by display name" rule.

**Alternatives:** (a) A separate `connection manage-access` CLI verb — rejected: the docs specify
`edit --access-policy`; a new verb would diverge from the documented surface. (b) Persist display names
alongside ids for Web display — accepted as a denormalized, display-only cache on the child row
(`SlackConnectionAllowedMemberRow` may carry a `DisplayName` snapshot), but it is never used for
authorization.

## Risks / Trade-offs

- **[Allowlist/Anyone add a `users.info` (+ `conversations.info` for Anyone) call per non-Owner invocation (D3)]**
  -> owner_only (the default) stays zero-extra-call; only opted-in connections pay. The calls are behind
  inbox dedup and the read is small. Cacheable later only if ingress volume demands it.
- **[Slack API failure denies a valid member (D3 safe degradation)]** -> a transient Slack outage makes
  allowlist/anyone invocations reject until Slack recovers. This is the safe direction (an unverifiable
  identity must not exercise the Agent's configured authority); the Owner can still invoke (owner check
  needs no API call). Documented trade-off; the connection diagnostic already surfaces Slack reachability.
- **[Anyone disclosure is client-enforced, not server-enforced (D4)]** -> a client that skips the
  disclosure could apply anyone directly. Mitigation: the disclosure text is a documented server contract
  field and the official Web/CLI always render it; the Server trusts its own control plane (project
  operators), consistent with all other management routes. A server-side confirmation token is a follow-up
  if audit demands it.
- **[Whole-list replace deletes then inserts in one transaction (D4)]** -> a mid-transaction crash leaves
  either the old or new list, never a partial set (single `DbContext` transaction). Idempotent on retry
  only if the client resends the full intended list (which is the CLI contract).
- **[Channel stop authority needs a grain call to read the initiator (D5)]** -> one `GetInitialLaunchAsync`
  per stop request; stops are rare relative to launches/follow-ups. The initiator is immutable per
  session, so the result is stable.
- **[Owner is never stored in the allowlist (D1)]** -> the Owner cannot be "removed" because there is
  nothing to remove; the decider unconditionally authorizes Owner. This is the intended invariant, not a
  gap; Manage-access inputs that include the Owner's id are accepted (idempotent) or filtered, never
  stored as a removable row.
- **[Each channel message produces one ingress call per mentioned Connection (inherited from 515)]** ->
  the access decision runs once per such call; non-attributed Connections no-op early as today. No change
  to the fan-out cost.

## Migration Plan

1. **Storage (D1):** add `AccessPolicy` column + kind constants to `AgentConnection`/`AgentConnectionRow`,
   thread through `UpdateAsync`/`ToRow`/`ToDomain` (non-immutable); add `SlackConnectionAllowedMemberStore`
   + row + `DbSet` + unique index; EF migration (additive). Existing connections default to `owner_only`
   (column default), preserving today's behavior exactly.
2. **Access decider (D2, D3):** add `SlackConnectionAccessDecider`; replace the 5 channel sites + the DM
   gate with `EvaluateAsync`; wire `ISlackApiClient`/`ISecretStore` (already on `HandleChannelIngressRequest`;
   add to `HandleDmIngressRequest` if DM also routes through the decider). Register the new store as the
   7th `IAgentConnectionProviderCleanup` forward-registration.
3. **Manage-access endpoint (D4):** `POST /{id}/manage-access` with validate-then-replace; extend the
   connection read model with `accessPolicy`, `allowMembers`, and `anyoneDisclosure`.
4. **Channel stop (D5):** add the control branch to the channel state machine; reuse
   `TryGetTurnControlCommand` + `ExecuteTurnControlAsync`; authority via `GetInitialLaunchAsync`.
5. **CLI/Web (D6):** CLI flags routed to manage-access; Web panel + member search + disclosure.
6. **Tests + docs:** extend `RecordingSlackApiClient` (SpecTests) with settable per-user `UsersInfo` and
   a settable `ConversationsInfo` so Anyone/Allowlist specs can vary member status and Bot-in-channel;
   new specs cover each policy's accept/reject, DM-hardening, no-resource-on-deny, immediate-effect,
   no-name-auto-succession, and channel-stop authority. Close the 实装差距 notes in
   `docs/agent-connections.md:307-311,387-392` and `design/slack-agent-connection.md:152-168`.

**Rollback.** The change is additive (new column with a safe default, new child table, new endpoint,
new decider) plus a consolidation of inlined checks into that decider. Revert restores Owner-only
behavior: existing connections already carry `owner_only`, and removing the decider restores the
equality checks. No Agent/Job/Session loses addressability; no stored Slack data is rewritten.

## Open Questions

- **Manage-access authority model (D4):** v1 treats Manage access as a Mohist control-plane (project
  operator) action, consistent with all other management routes, and enforces "only via explicit Manage
  access / not via Slack message" behaviorally. Confirm whether a future Slack-native manage surface needs
  a Slack-owner bearer on this endpoint; if so, that is a separate follow-up.
- **Anyone disclosure enforcement (D4):** confirm whether audit/compliance requires a server-side
  confirmation token for `anyone`, or whether the documented client-enforced contract suffices for v1.
- **Allowlist member display snapshot (D6):** confirm whether the child row stores a denormalized
  `DisplayName` snapshot for Web rendering, refreshed on add/Manage-access (display only, never
  authorization), or whether Web resolves names live.
- **Channel stop keyword form (D5):** confirm the exact trigger — bare "stop"/"cancel" in a bound thread,
  and "@bot stop"/"@bot cancel" for a root mention — and whether a localized alias set is needed in v1.
- **Allowlist reconciliation cadence (Non-Goal):** v1 validates live at invocation; confirm a follow-up
  owns any background re-validation or stale-member surfacing in diagnostics.
