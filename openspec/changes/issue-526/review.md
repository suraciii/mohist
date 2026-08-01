# Review: Issue 526 — Slack channel access policies

## Verdict

**FAIL.** The server-side implementation (T-001, T-002, T-004, and the server half of
T-003) is well-built, internally consistent, and fully green (SpecTests 3672, UnitTests
1723, ArchTests 51, Cli.Tests 1497, Web 5147, Runner 1510; `npm run build` succeeds).
However, **the entire CLI and Web surface of T-003 is missing**, violating multiple
explicit acceptance criteria of the issue. `packages/cli` and `packages/web` have zero
changed files. This is acknowledged in `openspec/changes/issue-526/progress.txt:28`
("T-003 not done (interrupted): CLI `--access-policy` / `--allow-member` flags, the
Anyone disclosure confirmation gate, ... the Web access-policy section, the member-search
widget, CLI tests, Web tests, and the final integration verification across the three
layers").

Findings below. Blockers first, then observations.

---

## Findings — must fix before merge

### B1. CLI `connection edit` access-policy flags are not implemented (T-003)

T-003 acceptance criteria require:

> "The CLI connection edit command accepts `--access-policy` and a repeatable
> `--allow-member`, routes them to the manage-access endpoint (separate from the
> bot-name/avatar-hash PATCH), renders the `anyoneDisclosure` and requires confirmation
> before sending `anyone`, and errors before the call when `--allow-member` is supplied
> with `owner_only` or `anyone`."

`proposal.md:32-34` also lists this as a deliverable ("CLI `connection edit` 增
`--access-policy` 与可重复 `--allow-member`"). There is **no** change to `packages/cli`.
A repo-wide search for `access-policy` / `accessPolicy` / `manage-access` / `allow-member`
in `packages/cli` returns nothing. The server endpoint `POST /{id}/manage-access`
(`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:297`) and the
`anyoneDisclosure` contract field exist but have no CLI consumer.

**Action:** add the `--access-policy` / repeatable `--allow-member` flags to the CLI
`connection edit` command, route them to `POST /{id}/manage-access` (separate from the
existing `PATCH` for `--bot-name`/`--avatar-hash`), render
`SlackConnectionAccessContract.AnyoneDisclosure`
(`packages/server/src/Mohist.Server/Slack/SlackConnectionAccessManager.cs:13`) and gate
`anyone` behind confirmation, and reject `--allow-member` with `owner_only`/`anyone`
client-side before the call. Add CLI tests covering flag routing, the confirmation gate,
and the incompatibility error.

### B2. Web Connection access-policy panel is not implemented (T-003)

T-003 acceptance criteria require:

> "The Web Connection panel renders the current policy, an allowlist editor backed by
> member name-and-avatar search that resolves to stable Slack identities, and the Anyone
> disclosure; display name and avatar are presentation only and are never the authorization
> identity."

`proposal.md:87-88` lists the Web panel as a deliverable. There is **no** change to
`packages/web`. The server already exposes the pieces the Web needs — the read-model fields
on the manage-access response (`SlackConnectionRoutes.cs:317-323`) and the member-search
endpoint `GET /{id}/members?q=...` (`SlackConnectionRoutes.cs:328-339`,
`SlackMemberSearchService`) — but there is no Web widget consuming them.

**Action:** add the access-policy section to the Connection diagnostic page, a
member-search widget backed by `GET /{id}/members`, submit only stable Slack IDs to
`POST /{id}/manage-access`, and require an explicit confirmation checkbox before an
`anyone` mutation. Add Web tests (`npm run typecheck -w packages/web` and
`npm run test:ci -w packages/web`).

### B3. Cross-layer end-to-end verification is not possible (T-003)

T-003 acceptance criteria require:

> "End-to-end verification: after the Owner sets an allowlist via the endpoint a listed
> member's invocation is accepted, and after the Owner removes that member the member's
> next input is rejected; `npm test`, `npm run typecheck -w packages/web`, and
> `npm run test:ci -w packages/web` pass."

The server-side halves are verified independently (`SlackAccessPolicyT003Specs` covers the
endpoint; `SlackAccessPolicyT002Specs` covers invocation accept/reject), but the
end-to-end path through CLI → endpoint → invocation cannot be exercised because the CLI
and Web layers (B1/B2) do not exist. Web typecheck/test commands were not run as part of
this change because there is no Web change to validate.

**Action:** completed implicitly once B1 and B2 land; the E2E acceptance criterion is only
satisfiable with both layers present.

---

## Findings — observations (not blockers)

### O1. Manage-access read model does not synthesize the Owner into `allowMembers`

The Owner is structurally never stored (`SlackConnectionAccessDecider` authorizes the Owner
unconditionally; `SlackConnectionAccessManager.ReplaceAsync` filters the Owner id out before
insert at `SlackConnectionAccessManager.cs:71-73`). The manage-access response returns
`allowMembers` from `ListMembersAsync`, which reads only stored rows, so the Owner is **not**
present in the returned `allowMembers`. The `connection-access-management` spec scenario
"The Owner is re-added automatically after a replace" / "the resulting allowlist still
contains the Owner" is satisfied at the *authorization* level (the Owner is always
authorized) but the *displayed* list omits the Owner. This is the open question flagged in
`self-review.md:63-66`. Not blocking on its own, but the CLI/Web layers (B1/B2) will need a
decision on whether to synthesize the Owner into the displayed allowlist; resolve it when
those land.

### O2. The access decision is evaluated eagerly before the turn-control branch

In `HandleChannelIngressAsync` the decision is computed once at
`SlackConnectionRoutes.cs:1225` for every channel message, including stop/cancel commands
that are then handled entirely by `TryDispatchChannelTurnControlAsync`
(`:1270`, `:1362`, `:1379`) without consulting `decision`. Under `allowlist`/`anyone` this
means 1–2 Slack API calls (`users.info`, and `conversations.info` under `anyone`) are made
and discarded for every stop/cancel gesture. This is a performance cost, not a correctness
bug — the turn-control authority check is self-contained (Owner-or-initiator) and does not
depend on the access decision. If Anyone/Allowlist ingress volume ever matters, the decision
could be lazily evaluated only on the non-control path.

### O3. Turn-control authority intentionally bypasses the access policy (by design, confirmed consistent)

`TryDispatchChannelTurnControlAsync` runs before the `!decision.Allowed` rejection
(`:1270-1274`). Consequently a member who was the session initiator but has since been
removed from the allowlist can still stop their own active Turn. This matches the
`channel-session-stop` spec, which scopes stop authority to "the Connection Owner or the
Slack member who initiated that AgentSession" without tying it to current allowlist
membership, and the `channel-access-policy` "immediate effect" requirement speaks of
"inputs"/"follow-ups" (not control gestures). Flagged for awareness; no change needed.

### O4. `senderOwnsCurrentConnection` now means "sender is authorized", not "sender owns"

In the multi-bot-mention (`SlackConnectionRoutes.cs:1240`) and multi-binding-thread
(`:1339`) branches, the old `sender == connection.OwnerSlackUserId` was replaced by
`decision.Allowed`. Under `allowlist`/`anyone` an authorized non-Owner is now routed to the
ambiguous-prompt path instead of the non-owner path. This is a reasonable generalization
(an allowed member should be treated as authorized for the current Connection in the
ambiguous case) but the local variable name `senderOwnsCurrentConnection` is now
misleading. A rename (e.g. `senderAuthorizedForCurrentConnection`) would aid readability;
not a behavior defect.

---

## What was verified as correct

- **T-001 (storage + single Owner-only/DM decision):** `AgentConnection.AccessPolicy`
  (`AgentConnection.cs:22`) with `AccessPolicyKind` constants and `owner_only` default;
  threaded through `AgentConnectionStore.UpdateAsync` as a non-immutable field
  (`AgentConnectionStore.cs` diff) and `ToRow`/`ToDomain` with empty-string fallback;
  additive migration `20260801120000_AddAccessPolicyAndAllowedMembers.cs`;
  `SlackConnectionAllowedMemberStore` implements `IAgentConnectionProviderCleanup`
  (`DeleteForConnectionAsync`) and is registered as the 7th forward-registration
  (`MohistServiceRegistration.cs:112`); the five channel owner-check sites + the DM gate in
  `SlackOwnerClaimService` route through `SlackConnectionAccessDecider.EvaluateAsync`.
- **T-002 (allowlist/anyone live validation):** `EvaluateAllowlistAsync` /
  `EvaluateAnyoneAsync` reuse `SlackOwnerClaimService.IsEligibleMember` and
  `UsersInfoAsync`/`ConversationsInfoAsync`; safe-deny on not-OK/throw/null
  (`SlackConnectionAccessDecider.cs:159-225`); Owner short-circuits before any API call;
  no caching.
- **T-004 (channel turn control):** `TryDispatchChannelTurnControlAsync` detects standalone
  stop/cancel after mention stripping, authorizes by Owner or
  `GetInitialLaunchAsync().Input.Provenance.MemberId`, reuses
  `ResolveCurrentTurnControlAsync`/`ExecuteTurnControlAsync`, and handles no-active-work /
  already-ended / redelivery cases.
- **T-003 server half:** `POST /{id}/manage-access` validates every member before a single
  transaction deletes-then-inserts; `owner_only`/`anyone` + `allowMembers` is rejected with
  `allow_members_not_allowed`; invalid members rejected with `invalid_allow_member`;
  `GET /{id}/members` delegates to `SlackMemberSearchService`. `ApiResults.Fail` now always
  emits a `code` (`ApiResponse.cs`).
- **Tests:** `SlackAccessPolicySpecs`, `SlackAccessPolicyT002Specs`,
  `SlackAccessPolicyT003Specs`, `SlackConnectionAccessDeciderSpecs`,
  `SlackChannelTurnControlSpecs` added; `RecordingSlackApiClient` extended with per-user
  `UsersInfo` and `ConversationsInfo`; all green. No real Slack/network/process/wall-clock
  dependencies observed.

<promise>FAIL</promise>
