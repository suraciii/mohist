## Context

Issue 517 makes an already-installed Slack Connection operable without deleting and recreating it. The #514 vertical delivered install → claim → dispatch → result; this issue delivers the day-two operations: credential rotation, owner transfer, disable/enable, delete-boundary confirmation, and a unified diagnostic face. The product spec is `docs/agent-connections.md`; the binding architecture is `design/slack-agent-connection.md`.

Current state (sourced from the #514 implementation):

- **The four-facts model is persisted but under-exposed.** `AgentConnection` (`Agent/Domain/AgentConnection.cs:3`) carries `SetupProgress`, `DesiredState` (default `Enabled`), `ConnectionHealth`/`HealthReason`, `AgentReadiness`, plus `OwnerSlackUserId?` and `LastHeartbeatAt?`. The store's `UpdateAsync` (`AgentConnectionStore.cs:117`) already accepts `desiredState` — but no route or CLI command toggles it. `view`/`list` return the raw domain object; consumers must interpret the fields themselves.
- **`configure` is a silent overwrite with no synchronous verification.** `SlackConnectionRoutes.cs:88` stores App+Bot tokens into `ISecretStore` and flips `SetupProgress → WaitingForSlackService`. Verification (`SlackSetupVerifier.VerifyAsync`, `Slack/SlackSetupVerifier.cs:25`) runs only on the next adapter `adapter-session` heartbeat (`SlackConnectionRoutes.cs:247`), asynchronously. Nothing checks that new tokens resolve to the same workspace/App/Bot before accepting them — `BindSlackIdentityAsync` (`AgentConnectionStore.cs:160`) has the `immutable_binding` guard, but it fires inside the async verify path, after the tokens are already stored and Setup has regressed.
- **Owner claim is one-shot.** `SlackOwnerClaimService.GenerateAsync` (`Slack/SlackOwnerClaimService.cs:51`) throws `InvalidOperationException` when `OwnerSlackUserId is not null || SetupProgress == Complete`. `TryClaimAsync` (`:134`) atomically sets the owner with a `WHERE OwnerSlackUserId == null` guard — there is no path to swap an existing owner.
- **Adapter discovery already filters on Enabled.** `ListForAdapterAsync` (`AgentConnectionStore.cs:69`) returns only `DesiredState == Enabled` connections, so a Disabled connection naturally drops from the adapter — but the ingress route (`SlackConnectionRoutes.cs:119`) never checks `DesiredState`, so a disabled connection would still accept input if the adapter somehow forwarded an event.
- **Delete cascade is implemented.** `DeleteAsync` (`AgentConnectionStore.cs:201`) soft-deletes and runs `IAgentConnectionProviderCleanup` implementations (secrets, inbox, outbox, claim codes). It preserves Agent/Job/Session/Input/Turn. No diagnostic wording about Slack-side App uninstall exists.
- **No Web surface for Connections exists.** Repo-wide search of `packages/web/src` returns zero Slack/AgentConnection hits.
- **Identity data available at verification time is discarded.** `SlackSetupVerifier.VerifyAsync` calls `BotsInfoAsync` (`:42`) and gets `bot.Bot.Name` but does not persist it — there is no "last-verified Slack-side name" to compare against for drift detection.
- **Slack API methods available for probing.** `ISlackApiClient` (`Slack/ISlackApiClient.cs:7`) exposes `AuthTestAsync`, `BotsInfoAsync`, `PermissionsScopesListAsync`, `UsersInfoAsync`, `ConversationsInfoAsync`, `UsersListAsync`. `UsersInfoAsync` returns `SlackUserInfo` (`:64`) with `IsBot`, `Deleted`, `IsRestricted`, `IsUltraRestricted`, `IsGuest`, `TeamId`, `TeamIds` — sufficient for owner-availability probing. `SlackOwnerClaimService.IsEligibleMember` (`:166`) is the existing membership-eligibility predicate.
- **Reusable patterns.** `AgentReadinessDeriver.Derive` (`Agent/Services/AgentReadiness.cs`) is a pure function deriving readiness from Agent config — the shape for a diagnostic pure function. `AgentConnectionDispatchDecision.For` is a pure accept/reject decision — the shape for diagnostic precedence. CLI credential reading (`MohistCliCommands.AgentConnection.cs:191` `ReadCredentialsAsync` + `IsProtectedFile`) is reusable for rotate-credentials. The spec test `CliAgentConnectionCommandSpecs.cs:18-21` explicitly asserts rotate/transfer/enable/disable commands do NOT exist yet.

## Goals / Non-Goals

**Goals:**

- Credential rotation: submit new tokens, verify synchronously against Slack, reject if identity differs from the bound workspace/App/Bot, store only on success, preserve Owner and accepted work.
- Owner transfer: generate a fresh single-use claim for an already-owned Connection, atomic swap (old owner effective until new owner claims), same workspace regular-member validation, no auto-transfer on departure.
- Enable/Disable: expose DesiredState toggle via route + CLI; immediate ingress and adapter-discovery enforcement; preserve accepted work; no replay on enable.
- Unified diagnostic: a pure priority-ordered computation that surfaces one most-important state + reason + one next action across setup, service, credentials, owner availability, identity drift, and Agent readiness; consumed by Web and CLI.
- Delete-boundary confirmation: the existing cascade is correct; add honest diagnostic wording (App not uninstalled on Slack side).

**Non-Goals:**

- Backlog, backpressure, Degraded(Backpressured), and Delivery uncertain presentation/handling (reliability issue).
- Persistent event/thread-mapping/outbox caching in the Slack adapter process.
- Auto-modifying Slack App name/avatar or uninstalling the App from Slack.
- Public Marketplace, multi-tenant hosting, billing, cross-org identity federation.
- DM continuous conversation, current-Session mapping, New task switching.
- Channel mention, thread follow-up, Allowlist/Anyone access policy.

## Decisions

### D1. Credential rotation is a dedicated `rotate-credentials` route that verifies-before-store

Add `POST /api/projects/{projectRef}/slack-connections/{connectionId}/rotate-credentials` (body: `SlackCredentialsBody`, same shape as configure). The route:

1. Loads the current connection and requires that identity is bound (i.e. `WorkspaceTeamId`, `AppId`, and `BotUserId` are populated — the same `HasBoundIdentity` check the store uses in `BindSlackIdentityAsync`, `AgentConnectionStore.cs:236`). Rotation is for connections whose identity was established by a prior successful verification, regardless of current `SetupProgress` (a connection that regressed to `FixSlackSetup` after credential expiry still has a bound identity and should rotate, not re-configure).
2. Calls a new `SlackSetupVerifier.VerifyRotationAsync(projectId, connectionId, newAppToken, newBotToken, ct)` that runs the full Slack verification (auth.test → bots.info → scopes) **using the supplied tokens directly** — it does NOT read from or write to the secret store. It returns a `RotationCheckResult { Ok, Reason, ResolvedTeamId, ResolvedAppId, ResolvedBotUserId }`.
3. The route checks `ResolvedTeamId/AppId/BotUserId` against the connection's bound `WorkspaceTeamId/AppId/BotUserId`. If any differ → `400 credential_binding_mismatch`, do not store.
4. If scope check fails or Slack rejects the tokens → `400 credential_verification_failed` with the concrete reason, do not store.
5. On success → store both tokens via `ISecretStore.StoreAsync`, clear `HealthReason` if it was credential-related, return the updated connection.

`configure` (`SlackConnectionRoutes.cs:88`) stays unchanged for initial setup but gains a guard: if identity is already bound (the same `HasBoundIdentity` check), it returns `409 use_rotate_credentials` pointing the operator to `rotate-credentials`. This prevents the silent-overwrite regression where calling `configure` on a connection with a bound identity resets `SetupProgress → WaitingForSlackService` and defers verification to the next heartbeat. Checking bound-identity state (rather than specific `SetupProgress` values) closes the edge case where a once-verified connection regressed to `FixSlackSetup` after credential expiry — such a connection still has a bound identity and should rotate, not re-configure.

**Rationale:** verify-before-store is strictly safer than store-then-verify-then-rollback: there is no window where invalid tokens are in the store, and no rollback path to get wrong. A dedicated route keeps the initial-setup and rotation semantics cleanly separated (different preconditions, different verification timing). `VerifyRotationAsync` reuses the same Slack calls and scope list as `VerifyAsync` but parameterizes the token source and skips the `BindSlackIdentityAsync` call (identity is already bound; rotation must not rebind).

**Alternatives:** (a) Extend `configure` to detect already-verified connections and run synchronous verification — rejected: mixes two preconditions (unverified vs verified) in one route and makes the "does this regress SetupProgress?" question branch-dependent. (b) Store-then-verify-then-rollback — rejected: introduces a window of invalid credentials in the store and a rollback path that must restore the exact prior bytes; verify-before-store avoids both.

### D2. Owner transfer via transfer-kind claim codes and a conditional atomic swap

Extend `SlackOwnerClaimCodeRow` with a `Kind` column (`initial` | `transfer`). Modify `SlackOwnerClaimService`:

- **`GenerateAsync(projectId, connectionId, kind, lifetime?, ct)`** — when `kind == transfer`, requires `OwnerSlackUserId is not null && SetupProgress == Complete` (the opposite precondition of initial claim). Generates a new code with `Kind = transfer`, supersedes prior unused codes of the same kind. Returns `SlackOwnerClaimCode`.
- **`HandleInboundDmAsync`** — when a code matches and `code.Kind == transfer`, calls a new `TryTransferAsync` instead of `TryClaimAsync`. `TryTransferAsync` runs the same `IsEligibleMember` check on the sender, then atomically swaps the owner via `ExecuteUpdateAsync` with `WHERE OwnerSlackUserId == currentOwner` (instead of `WHERE OwnerSlackUserId == null`). If `changed == 0` (concurrent transfer or owner already swapped), rejects. On success, marks the code used. The old owner loses privileges in the same operation.
- **Initial claim path unchanged:** `code.Kind == initial` continues to use `TryClaimAsync` with the `WHERE OwnerSlackUserId == null` guard.

The new `transfer-owner` route (`POST /{id}/transfer-owner`) calls `GenerateAsync(..., kind: transfer)`. The existing `claim-owner` route continues to call `GenerateAsync(..., kind: initial)`.

**Rationale:** the claim-code table and the DM-based redemption flow already exist; adding a `Kind` discriminator and a second atomic-update guard reuses the entire mechanism. The conditional `WHERE OwnerSlackUserId == currentOwner` makes the swap atomic without a no-owner window — the old owner is effective right up until the row updates, and the new owner is effective immediately after.

**Alternatives:** (a) Add a separate `SlackOwnerTransferCodeRow` table — rejected: duplicates the code-generation, hashing, expiry, and supersession logic. (b) Revoke the old owner first, then claim — rejected: creates a no-owner window where the Connection accepts no input.

### D3. Owner availability is a lazy diagnostic-time probe, not a persisted fact

Owner availability (is the bound Owner still a current regular member?) is computed by calling `ISlackApiClient.UsersInfoAsync(ownerSlackUserId, botToken)` and running `SlackOwnerClaimService.IsEligibleMember` against the result. This probe runs only when a diagnostic is explicitly requested (single-connection `view`/`diagnostic`), not on every `list`. The result is ephemeral — it is not stored on the connection. If the Bot token is unavailable or Slack is unreachable, the diagnostic reports "owner availability unknown" rather than guessing.

**Rationale:** owner status can change at any time on the Slack side; persisting a snapshot would be stale within minutes. A lazy probe is always current. Limiting it to single-view avoids N Slack API calls on a list endpoint. Storing it as a fact would violate the "four independent facts" model — availability is a derived observation about an external system, not a Connection-owned state.

**Alternatives:** (a) Persist `OwnerAvailable` as a fifth fact refreshed on each adapter heartbeat — rejected: adds a persisted fact that is stale by definition and conflates "what Mohist chose" (DesiredState, SetupProgress) with "what Slack reports right now." (b) Have the adapter probe owner status on heartbeat — rejected: owner-eligibility is Server authority (`design/slack-agent-connection.md:47`), not adapter responsibility.

### D4. Enable/Disable as explicit routes plus an ingress DesiredState guard

Add two routes:
- `POST /api/projects/{projectRef}/slack-connections/{connectionId}/disable` → `UpdateAsync(..., desiredState: Disabled)`.
- `POST /api/projects/{projectRef}/slack-connections/{connectionId}/enable` → `UpdateAsync(..., desiredState: Enabled)`.

Both are idempotent (disabling an already-Disabled connection succeeds; enabling an already-Enabled one succeeds). Neither touches credentials, Owner, Setup progress, health, or accepted work.

Add a DesiredState guard to the ingress route (`SlackConnectionRoutes.cs:119`): immediately after loading the connection and before any claim/owner/dispatch logic, if `DesiredState == Disabled`, return `{ kind: "rejected", reason: "This Connection is disabled." }` with HTTP 200 (the adapter acks Slack; no inbox entry, no AgentJob). This is distinct from the backpressure 409 (`slack_backpressured`) — a disabled connection is a deliberate user choice, not a capacity anomaly.

Adapter discovery already excludes Disabled connections (`ListForAdapterAsync` filters on `Enabled`), so the adapter stops initiating Socket Mode sessions and stops claiming deliveries for them. No adapter change needed.

**Rationale:** the DesiredState field and the discovery filter already exist; the only missing pieces are the toggle routes and the ingress guard. Keeping disable-rejection at HTTP 200 (not an error) matches the existing rejection pattern for non-owner DMs — the adapter acks Slack, the user gets a reply, no retry storm.

**Alternatives:** (a) Return 409 for disabled ingress — rejected: 409 signals a conflict the caller should retry; a disabled connection should not be retried until explicitly enabled. (b) Modify the adapter to check DesiredState — rejected: the adapter is stateless and discovers connections via the filtered list; adding a client-side check duplicates the Server's authority.

### D5. Delete-boundary confirmation and honest Slack-side wording

The existing `DeleteAsync` cascade (`AgentConnectionStore.cs:201` → `DeleteProviderRecordsAsync` → secrets + `IAgentConnectionProviderCleanup` implementations) is correct: it removes credentials, inbox, outbox, and claim codes, and preserves Agent/Job/Session/Input/Turn/attachments. No data change needed.

The diagnostic and CLI delete output SHALL state that deletion removed Mohist-side records and that the Slack App remains installed on the workspace until a workspace admin manually removes it. This is a presentation change (response body / CLI message), not a behavior change.

**Rationale:** the spec requires the diagnostic to "not pretend the App is uninstalled." The cascade already preserves the right things; only the messaging was missing.

### D6. Diagnostic is a pure priority-ordered computation with a dedicated route

Add `Agent/Services/ConnectionDiagnostic.cs` — a pure function `Compute(AgentConnection connection, DiagnosticInputs inputs)` returning `ConnectionDiagnosticResult { PrimaryState, Reason, NextAction, Facts }`. `DiagnosticInputs` carries the lazily-probed owner availability (D3), the derived Agent readiness, and the heartbeat-freshness check (`SlackSetupVerifier.IsAdapterOnline`). The function applies a fixed precedence to select the single most-important state:

| Priority | Condition | Primary state | Next action |
|---|---|---|---|
| 1 | `SetupProgress != Complete` | Setup incomplete | Advance the current setup step |
| 2 | `ConnectionHealth == Unhealthy` AND `HealthReason` indicates credential/scope/App-Bot failure | Credentials invalid | Rotate credentials |
| 3 | adapter offline (stale heartbeat) OR `ConnectionHealth == Unhealthy` with a service-unreachable reason (e.g. "Slack could not be reached") | Service offline | Start mohist-slack / check Slack connectivity |
| 4 | Owner unavailable (D3 probe) | Owner unavailable | Transfer ownership |
| 5 | `AgentReadiness == NeedsSetup` | Agent needs setup | Configure Agent runtime/model |
| 6 | `DesiredState == Disabled` | Disabled | Enable the Connection |
| 7 | Identity drift detected (D7) | Identity drift | Review the name/avatar difference |
| 8 | none of the above | Healthy | No action needed |

Priority 2 and 3 both cover `ConnectionHealth == Unhealthy` but split on the `HealthReason` content: `SlackSetupVerifier.FailAsync` produces reasons like "Slack rejected the Bot token" (credential), "Slack is missing required scopes" (credential), and "Slack could not be reached" (service). The diagnostic inspects the reason string to classify each case, so a service-unreachable failure does not produce a misleading "Rotate credentials" next action.

The function does not collapse facts into a `Connected` value; `Facts` exposes all independent dimensions so the UI can show supporting detail. Degraded (backpressure) health surfaces as a supporting fact, not as the primary state (it is a reliability-issue concern, not this issue's).

Add `GET /api/projects/{projectRef}/slack-connections/{connectionId}/diagnostic` that loads the connection, runs the D3 owner probe, derives readiness, checks heartbeat freshness, and returns `ConnectionDiagnosticResult`. `view` continues to return the raw connection; `list` returns raw connections (no per-row Slack probes). The CLI `view` command calls the diagnostic endpoint and renders the summary; `list` renders a compact per-row primary-state column derived from stored facts only (no live probes).

**Rationale:** a pure function is independently testable with injected inputs (no Slack, no DB, no time — the inputs are pre-computed). The precedence is ordered by "can anything else work if this is broken?" — setup blocks everything; invalid credentials block all Slack interaction; an offline service blocks event flow; an unavailable owner is a security risk; an unconfigured Agent blocks dispatch; disabled is intentional; drift is informational. Keeping `list` probe-free avoids N×Slack-API-calls and respects rate limits.

**Alternatives:** (a) Fold the diagnostic into `view` (always compute on GET) — rejected: `view` is also used by scripts and the adapter-adjacent tooling that expect the raw object; a separate endpoint lets the diagnostic evolve without breaking the raw contract. (b) Store a computed `PrimaryState` column — rejected: it is a pure derivation of other facts plus a live probe; storing it creates a stale-value problem and a second source of truth.

### D7. Identity drift detected from verification-time snapshots (name and icon)

Add `VerifiedBotName` and `VerifiedBotIconUrl` (both nullable strings) to `AgentConnection` and `AgentConnectionRow`. Extend `SlackBotInfo` (`ISlackApiClient.cs:61`) with an `IconUrl` field populated from the `icons` object in the `bots.info` response (pick the highest-resolution image URL available). `SlackSetupVerifier.VerifyAsync` and `VerifyRotationAsync` capture both `VerifiedBotName` (from `bot.Bot.Name`) and `VerifiedBotIconUrl` (from `bot.Bot.IconUrl`) on every successful verification.

The diagnostic detects three drift kinds:

- **Presentation name drift:** `VerifiedBotName` (what Slack actually shows) ≠ `BotName` (what the operator configured on the Connection).
- **Agent-name drift:** `BotName` (the Bot's configured display name) ≠ `Agent.Name` (the bound Agent's name).
- **Avatar drift:** `VerifiedBotIconUrl` (what Slack reports) ≠ `AvatarHash` (what the operator recorded on the Connection) — the same two-source pattern as name drift.

All three are surfaced as diagnostic facts with the concrete values shown. Mohist does NOT modify the Slack side or overwrite `BotName`/`VerifiedBotName`/`VerifiedBotIconUrl` to mask the difference.

Avatar drift follows the name-drift pattern: `VerifiedBotName` vs `BotName` compares a Slack-side observation against an operator-configured value, and `VerifiedBotIconUrl` vs `AvatarHash` does the same for avatars. The values may be different representations (URL vs hash); the diagnostic surfaces both honestly so the operator can visually compare — the mismatch itself is the drift signal.

**Rationale:** capturing the Slack-side name and icon at verification time (which already calls `bots.info`) is zero-cost — no extra API call. The snapshots are refreshed on every heartbeat-triggered verification, so they track Slack-side renames and icon changes without a dedicated polling loop. `VerifiedBotName` and `VerifiedBotIconUrl` are observations (what Slack reports), not Mohist-owned settings, so they sit alongside `BotName` (what the operator chose) without conflating them.

**Alternatives:** (a) Live `bots.info` probe at diagnostic time — rejected: adds latency and an API call to every diagnostic view for data that changes rarely. (b) Have the adapter report the observed name/icon on heartbeat — rejected: identity observation is Server authority; the adapter is a protocol translator. (c) Fetch and hash the icon image for a content-based comparison — rejected: adds an HTTP fetch + image processing per verification for marginal benefit; URL comparison detects the same changes at zero cost.

### D8. CLI gains rotate-credentials, transfer-owner, enable, disable; view/list consume diagnostic

Add four subcommands to `AgentConnectionCommands.Build` (`MohistCliCommands.AgentConnection.cs:9`):
- `rotate-credentials <id> [--credentials-file]` — reuses `ReadCredentialsAsync`/`IsProtectedFile` (`:191`, `:231`) unchanged; POSTs to `/rotate-credentials`.
- `transfer-owner <id>` — POSTs to `/transfer-owner`, prints the code + expiry.
- `disable <id>` / `enable <id>` — POST to the respective routes.

`view <id>` calls the `/diagnostic` endpoint and renders: primary state, reason, next action, then the supporting facts. `list` renders a compact table with a per-row primary-state column computed from stored facts only (no Slack probes). Update `CliAgentConnectionCommandSpecs.cs:18-21` to assert the four commands now exist (and remove the "absent" assertion).

**Rationale:** the credential-reading helper is already battle-tested by `configure`; reusing it for `rotate-credentials` keeps one protected-input path. The spec test's explicit absence assertion is the single gating test change.

### D9. Web diagnostic view as the first Connection surface

Introduce a Web route and component for the Connection diagnostic: a summary card (primary state, reason, next action) with expandable fact detail (setup progress, desired state, health, owner availability, identity drift, agent readiness). The Web calls `GET /{id}/diagnostic`. No Connection create/edit/disable/enable UI in this issue — those remain CLI-only; the Web surface is read-and-diagnose. This is the first Web code touching AgentConnection, so it also establishes the data-fetching hook (TanStack Query) and the API client function for connections.

**Rationale:** the spec requires Web to present the diagnostic summary. Starting with read-only diagnostic avoids coupling this issue to a full Connection management UI (create/edit forms, credential file upload in-browser) which is a larger effort. The diagnostic endpoint gives the Web everything it needs in one call.

## Risks / Trade-offs

- **[Verify-before-store means a rotation call holds new tokens in request memory (D1)]** -> tokens are request-scoped and never logged (the existing `*token` redaction guard covers them); on failure they are discarded with no persistence.
- **[Owner availability probe hits Slack on every single-view diagnostic (D3)]** -> one `users.info` call per view is within Slack rate limits; if Slack is unreachable, the diagnostic degrades gracefully to "owner availability unknown" rather than failing the whole diagnostic.
- **[Transfer claim code Kind column requires a migration (D2)]** -> additive column with a default of `initial`; existing rows migrate cleanly; no data rewrite.
- **[VerifiedBotName and VerifiedBotIconUrl columns require a migration (D7)]** -> additive nullable columns; existing connections have `null` until next verification; diagnostic reports "not yet verified" for drift rather than a false positive.
- **[Diagnostic precedence is a product judgment call (D6)]** -> the precedence table is documented and testable; if field evidence shows a different priority (e.g., disabled should rank above owner-unavailable), it is a one-place change in the pure function.
- **[Disable does not cancel in-flight adapter deliveries already claimed (D4)]** -> a delivery claimed by the adapter before disable may still be sent; this is acceptable (it was accepted before disable) and matches "accepted work is preserved." The adapter will not claim new deliveries after discovery refresh excludes the connection.
- **[Avatar drift compares URL to hash (D7)]** -> `VerifiedBotIconUrl` (a Slack icon URL) and `AvatarHash` (an operator-provided value) may be different representations of the same image; the diagnostic surfaces both values honestly so the operator can visually judge, rather than masking or auto-resolving the difference.

## Migration Plan

1. **Server — model + migrations (D2, D7):** add `Kind` to `SlackOwnerClaimCodeRow` (default `initial`); add `VerifiedBotName` and `VerifiedBotIconUrl` to `AgentConnectionRow`; extend `SlackBotInfo` with `IconUrl`. EF migration is purely additive.
2. **Server — credential rotation (D1):** `SlackSetupVerifier.VerifyRotationAsync`; `rotate-credentials` route; `configure` guard for already-bound connections.
3. **Server — owner transfer (D2, D3):** extend `GenerateAsync` with `kind`; `TryTransferAsync`; `transfer-owner` route; owner-availability probe helper.
4. **Server — enable/disable + ingress guard (D4):** `disable`/`enable` routes; ingress DesiredState check.
5. **Server — diagnostic (D6, D7):** `ConnectionDiagnostic.Compute` pure function; `/diagnostic` route; `VerifiedBotName` and `VerifiedBotIconUrl` capture in `VerifyAsync`.
6. **Server — delete wording (D5):** response/message update.
7. **CLI (D8):** four new subcommands; `view`/`list` diagnostic rendering; spec test update.
8. **Web (D9):** diagnostic route + component + API client hook.
9. **Tests + docs:** fake `ISlackApiClient` (rotation verify, transfer membership, owner-availability probe, bots.info name + icon capture); fake adapter↔Server transport; injectable `TimeProvider` (heartbeat freshness, claim-code expiry); pure-function diagnostic tests (all precedence rows); update `docs/agent-connections.md` and `design/slack-agent-connection.md` 实装差距 sections.

**Rollback.** All changes are additive (new columns with defaults, new routes, new CLI commands, new Web route). Revert drops the new routes/commands; the `Kind=initial` and `VerifiedBotName=null`/`VerifiedBotIconUrl=null` columns are harmless orphans. No existing data is rewritten; no Agent, Job, Session, or accepted Input loses addressability.

## Open Questions

- **Diagnostic on `list`:** confirm that `list` should remain probe-free (stored-facts-only primary-state column) vs. a `--diagnose` flag that triggers per-row live probes for small connection counts.
- **Transfer claim lifetime:** initial claims default to 10 minutes; confirm the same lifetime for transfer claims or choose a longer window (transfers may involve coordination between operator and new owner).
- **Web scope (D9):** confirm that read-only diagnostic is the right Web scope for this issue, or whether minimal enable/disable buttons should accompany the diagnostic card.
