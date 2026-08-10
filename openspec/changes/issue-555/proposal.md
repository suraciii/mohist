## Why

The repository has canonical AgentJob and AgentSession execution paths, but no stable public boundary for a headless external Agent. Existing project-scoped routes use ordinary Mohist authentication and internal-shaped responses, so a lost response or connection can leave the caller unable to tell whether work was accepted, causing duplicate launches or follow-ups; the documented External Agent API now needs to become an explicit, secure contract.

## What Changes

- Add a versioned private `/api/v1` External Agent API for launching work, adding Session input, reading public Job/Input/Turn state, reading Session events, and stopping one Turn. All commands continue to use the canonical AgentJob and AgentSession lifecycle.
- Extend PAT issuance and request authorization with an explicit External Agent Project grant. Direct requests use Bearer PATs, enforce `readonly` or `operator` scope per route, and complete authentication and Project authorization before resource lookup, idempotency reconciliation, or admission.
- Require a caller-supplied `Idempotency-Key` for every write. Normalize the accepted request server-side, persist a durable key-to-canonical-identity mapping, return the original outcome for matching retries, reject changed payloads with a stable conflict, and retain definitive admission rejections under the key.
- Expose a strict public execution projection with the five aggregate states `accepted`, `queued`, `running`, `terminal`, and `unknown`, plus stable identifiers, safe timestamps, public output, and safe error codes. Internal prompts, Runtime and Runner identities, workspace data, operation keys, fences, and raw provider payloads remain private.
- Persist an ordered public event stream per Session with opaque exclusive continuation cursors. Reconnecting callers can resume after their last processed event, while invalid, expired, old-generation, and projection-lag conditions are explicit rather than silently replayed or reset.
- Fence Turn stop and terminal-result races so a repeated stop cannot issue a second effect, a late result cannot rewrite a terminal outcome, and unresolved external effects remain `unknown` without automatic replay.

## Capabilities

- `external-agent-authentication`: Bearer PAT identity, explicit Project grants, route scopes, authorization ordering, and safe unauthenticated/forbidden behavior for the direct API.
- `external-agent-execution`: Versioned launch, follow-up, read, and Turn-stop routes; canonical AgentJob/AgentSession ownership; public execution projections, states, and error allowlists.
- `external-agent-idempotency`: Stable write keys, server-computed request fingerprints, durable retry mappings, conflicting-reuse behavior, response-loss recovery, and fenced stop outcomes.
- `external-agent-session-replay`: Durable public Session events, opaque cursors, exclusive resume semantics, ordering and deduplication, projection consistency, stream generations, and retention failures.

## Impact

- **Server API and domain:** Add the versioned external routes and adapters around the existing `IAgentLauncher`, launch coordinator, AgentJob, AgentSession, and canonical stop services. Extend `AuthTokenRoutes`, credential models/stores, and request authorization with External Agent grants without creating a second execution lifecycle.
- **Persistence:** Add durable external request mappings plus the public execution snapshot, Session event journal, projection checkpoint, cursor/stream-generation metadata, and any required migrations. These records must remain separate from internal transcripts, Runner events, and UI event delivery.
- **CLI and documentation:** Extend PAT creation and listing to represent Project grants, and update `docs/auth.md`, `docs/agent-api.md`, and implementation-status guidance with the shipped contract. Existing Web, CLI, Agent Connection, and canonical Session routes remain the internal entry adapters.
- **Tests:** Add server and CLI coverage for grant enforcement and authorization order, all public states, duplicate and conflicting retries, stop races, projection lag, cursor resume/expiry, and privacy allowlists.
- **Dependencies:** No new external dependency is expected; the change uses the existing ASP.NET Core, Orleans, authentication, and persistence infrastructure.
