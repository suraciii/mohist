## Context

The server-side Slack Connection setup surface already exists from #514/#517: `POST /slack-connections` (create), `POST /{id}/configure` (store credentials), `POST /{id}/claim-owner` (mint owner code), and `GET /{id}/diagnostic` (recompute one most-important state + single next action from persisted facts). Setup is server-resumable — `AgentConnection.SetupProgress` (`Agent/Domain/AgentConnection.cs:16`) is a persisted state machine, and App/Bot tokens are stored AES-GCM encrypted (`AesGcmSecretStore`). The CLI drives all of this (`MohistCliCommands.AgentConnection.cs`).

The gap is entirely on the Web. `entities/agent-connection` exports only `getConnectionDiagnostic` (`api/client.ts:4`) — a single read. `AgentDetailPage` has no Connections entry; its right rail renders `SubscriptionsSection` via a `components` injection prop (`AgentDetailPage.tsx:42-45`, rendered at `:584`). There is no masked/secret input anywhere in `packages/web/src` (only the plain `shared/ui/components/input.tsx`). So a non-terminal user cannot create or advance a Slack Connection at all, and an interrupted setup has no Web place to resume.

One concrete server shortfall: `POST /slack-connections` (`SlackConnectionRoutes.cs:33-64`) echoes the caller-supplied `BotName`/`AvatarHash` and returns a generic `slackAppCreationReference` URL — it does **not** derive a Bot identity preview from the bound Agent. The Agent domain has `Name` and `Description` but **no avatar** (`Agent/Domain/Agent.cs:9-10`). No Slack mention-name / stable-suffix derivation logic exists anywhere today.

Stakeholders: Web (FSD-enforced, `npm run check:fsd`), Server Agent/Slack context, CLI/adapter/Runner (must be unaffected — they drive the same server facts).

## Goals / Non-Goals

**Goals:**
- Web is a parity Setup entry to the CLI: create, configure credentials, claim owner — all resumable.
- Agent detail page surfaces a Connections section that lists the Agent's Connections and offers Add Slack.
- Creating a Connection immediately yields a Bot identity preview derived from the bound Agent (name + description), with one author (Server) so Web and CLI agree.
- Credentials are captured through a protected, masked form — body-only, never persisted client-side, never read back.
- Setup is driven solely by server-persisted state; the Web keeps no divergent client step machine.
- The summary reuses the existing diagnostic `primaryState`/`nextAction` and keeps the four facts separately readable.

**Non-Goals:**
- Lifecycle ops already delivered in #517: credential rotation, owner transfer, disable/enable/delete UI.
- Channel access-policy management UI (Allowlist member picker, Anyone).
- Rendering Slack conversation content or transcripts in the Web.
- Auto-creating the Slack App or performing the workspace install for the user.
- Token prefix (`xapp-`/`xoxb-`) hard validation (authoritative check stays at Slack verification).

## Decisions

### A. A Connections widget injected into AgentDetailPage, mirroring agent-subscriptions

Add `widgets/agent-connections` with a `ConnectionsSection` component, structured exactly like `widgets/agent-subscriptions` (`SubscriptionsSection.tsx`): a card with a header, an **Add Slack** button, a list of the Agent's Connections, and an injectable `operationsHook` for testability. Add `ConnectionsSection` to `AgentDetailPageComponents` (`AgentDetailPage.tsx:42-45`) and render it in the right rail next to `SubscriptionsSection` (`:584`).

The section lists Connections from the existing `GET /slack-connections` (project-scoped list, already returns `AgentId` per row), filtered client-side by the Agent id. Each row shows lightweight state from the list response (`setupProgress`, `connectionHealth`) and links into the per-Connection surface (Decision B). It does **not** fetch a diagnostic per row — that is the detail surface's job.

**Alternative considered:** inline expand each Connection's full wizard inside the section. Rejected: setup is multi-step, resumable, and must be deep-linkable across devices; a stable URL (Decision B) serves "resume on another device" better than an inline accordion.

### B. The setup/resume surface extends the existing `/connections/:connectionId` page

The existing route `connections/:connectionId` (`App.tsx:74`) already renders `ConnectionDiagnosticPage`, which shows `primaryState`/`reason`/`nextAction` + the four-fact supporting panel (`ConnectionDiagnosticPage.tsx:77-106`). Extend that page from read-only diagnostic into the Setup surface: when the diagnostic's next action is "configure credentials", render the protected form (Decision E); when it is "claim owner", render the one-time-code generator (Decision F). Add Slack (Decision A) creates the Connection and navigates here.

This reuses the server-driven summary the page already renders, needs no new route, and gives setup a stable, resumable URL.

**Alternatives considered:**
- A brand-new `/agents/:id/connections/:cid/setup` route. Rejected: duplicates the diagnostic summary rendering and splits one Connection across two URLs.
- An inline Dialog on the Agent detail page. Rejected: a Dialog is poor for a multi-step, close-and-resume, cross-device flow and has no shareable URL.

### C. Server-side Agent identity derivation, single author for Web + CLI

Introduce a pure derivation that, given the bound Agent, produces a Slack Bot identity preview: a `botName` (sanitized to Slack's display-name constraints; when the Agent name is empty or violates the rules, a **stable suffix** derived deterministically from the Agent id is appended so re-derivation is idempotent) and an `appDescription` (the Agent description, or a generated non-empty generic fallback when blank). Wire it into `POST /slack-connections`: when the caller omits `BotName`, default the persisted `BotName` to the derived value, and add the derived preview (`botName`, `appDescription`) to the create response alongside the existing `slackAppCreationReference`.

The derivation lives in the Server so Web and CLI share one author and one rule set. It is additive and non-destructive to the immutable binding fields (`WorkspaceTeamId`/`AppId`/`BotUserId` are still confirmed only at verification, `SlackSetupVerifier.cs`).

**Avatar is deliberately not derived** — the Agent carries no avatar (`Agent.cs:9-10`), and the product spec states the avatar is applied manually in Slack App settings. The preview therefore shows the name-based identity and description, and points avatar configuration to Slack. (This reconciles the spec wording "identity … derived from the bound Agent": name + description are derived; avatar is configured in Slack, not derived.)

**Alternatives considered:**
- Derive the preview in the Web from the Agent fields it already has. Rejected: the CLI also needs the same preview and the same stable-suffix rule; two derivations would diverge, and the mention-name rule is a Server-owned domain invariant.
- Make `--bot-name` mandatory on create. Rejected: breaks CLI ergonomics and contradicts "Add Slack should not interrogate the user for a name".

### D. Server-driven setup: no client step machine, refetch for CLI parity

The Setup surface reads `primaryState`/`nextAction`/`facts` from `GET /{id}/diagnostic` and renders them directly. The Web holds **no** independent step counter — closing, refreshing, or returning on another device re-reads the server `SetupProgress`, so progress can never diverge. The ordered step view (Create app & add credentials → Waiting for Slack service → Fix Slack setup → Claim owner → Complete) derives `done`/`current`/`pending` from `facts.setupProgress`, reusing the `ProgressStages` derivation pattern (`entities/settings/ui/ProgressStages.tsx:17-48`).

Because a step may be completed from the CLI (or asynchronously when `mohist-slack` reconnects and `adapter-session` advances `SetupProgress`), the diagnostic query refetches on window focus and on a modest interval (matching the 5s cadence already used by `useAgentDetailStatus`). This is what makes "a step done on one side immediately holds on the other" true without a manual refresh.

**Alternative considered:** real-time push via the existing `ConnectionSubscriptionGrain` SignalR stream. Deferred: refetch-on-focus + interval is simpler and sufficient for a setup flow that is operator-paced; push can be added later without changing this decision's contract.

### E. Protected credential capture: new masked input, body-only, transient

Add a masked input to `shared/ui` (none exists today). The credential form is a controlled component whose state holds the two tokens only for the duration of the form. On submit it calls the existing `request()` helper (`shared/api/client.ts:18`) with `method: 'POST'` and a JSON `body` to `POST /{id}/configure` — tokens travel only in the request body, never in the URL/path/query (the helper already sets `Content-Type: application/json` and never serializes body into the URL). On success, local state is cleared and the diagnostic query is invalidated; the connection entity continues to expose only `credentialStatus` (from `facts`), never a token. No `GET` that returns a token is ever issued.

The masked input has **no reveal toggle** — stricter than a typical password field, matching the spec ("never displayed in cleartext"). Tokens are never written to `localStorage`/`sessionStorage`/URL, and no token value is logged.

**Alternatives considered:**
- A reveal ("show") toggle on the masked input. Rejected: the spec forbids cleartext display; a toggle reintroduces a cleartext path.
- Client-side `xapp-`/`xoxb-` prefix validation as a hard gate. Rejected (non-goal): Slack's verification is authoritative; a non-blocking format hint is the most that belongs here.

### F. One-time owner claim code held in local state only

`POST /{id}/claim-owner` returns `{ code, expiresAt }` (`SlackConnectionRoutes.cs:230-241`). The Setup surface shows the code in component state only while mounted; it is not persisted anywhere. Leaving/unmounting the surface discards it — recovering a lost code means regenerating, which server-side supersedes every prior unused code of that kind (`SlackOwnerClaimService.cs:96-100`). The claim itself happens in the Bot DM (unchanged server behavior); the Web only generates and displays the code.

### G. Create in Slack keeps the link, with the derived preview beside it

The create response's `slackAppCreationReference` remains the **Create in Slack** entry. Because Slack does not deep-prefill App name/description/avatar via URL, the surface shows the derived identity preview (Decision C) next to the link so the operator knows what to enter on Slack's side. A downloadable App manifest is a possible future enhancement, not v1.

## Risks / Trade-offs

- **Agent has no avatar** → the identity preview cannot show a real avatar. *Mitigation:* preview shows name + description and directs avatar configuration to Slack (consistent with the product spec); documented here so the spec's "avatar derived from the Agent" is read as name+description only.
- **Token exists transiently in browser component state** → *Mitigation:* masked, never rendered/persisted/logged, cleared on success; the form surface loads no third-party scripts. Within the app's existing trust boundary this is the same exposure any in-browser secret has.
- **`GET /diagnostic` probes Slack live (owner availability, heartbeat)** → can be slow when Slack is unreachable. *Mitigation:* the diagnostic already degrades to `unknown` (`ConnectionDiagnostic.cs:112`) and still derives the next action from persisted facts; refetch cadence stays modest.
- **Server derivation changes the create default** (omitted `BotName` is now derived, not empty) → *Mitigation:* deterministic, non-destructive, and gives Web/CLI parity; CLI callers who pass `--bot-name` are unaffected.
- **Create-in-Slack link is not prefilled** → operator transcribes name/description into Slack. *Mitigation:* derived preview shown beside the link; manifest download tracked as an open question.
- **"Immediately holds on the other side" depends on refetch** → *Mitigation:* refetch-on-focus + interval; operator-paced setup means a manual focus already covers the common case.

## Migration Plan

This change is additive on both sides and needs **no database migration**:

1. **Server:** add the identity-derivation pure type + wire into `POST /slack-connections` (default `BotName`, add preview to response). Existing routes, `SetupProgress` state machine, and `AesGcmSecretStore` are unchanged.
2. **Web:** add `widgets/agent-connections`, extend `entities/agent-connection` (create/configure/claim-owner client functions, mutation options, types), add the masked input to `shared/ui`, inject `ConnectionsSection` into `AgentDetailPage`, and extend `ConnectionDiagnosticPage` with the configure/claim affordances.
3. **Verify:** `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, `npm run check:fsd -w packages/web`; server spec coverage for the derived preview (fake Agent, no real Slack).
4. **Rollback:** revert the Web additions and the server derivation. No data to migrate back — the `BotName` column is unchanged; Connections created with a derived name keep working. CLI and adapter are untouched throughout (they drive the same server facts).

## Open Questions

- **Avatar preview wording:** confirm the preview is name + description only and directs avatar configuration to Slack (Agent has no avatar field). This reconciles the spec's "avatar derived from the bound Agent".
- **Token format hint:** add a non-blocking `xapp-`/`xoxb-` prefix hint in the form, or rely solely on Slack's authoritative verification?
- **Create-in-Slack prefill:** ship a downloadable App manifest in v1 to avoid transcription, or defer?
- **Refetch cadence:** is focus-refetch + a 5s interval the right balance for CLI-parity "immediately", or should the existing SignalR `ConnectionSubscriptionGrain` stream be wired now?
