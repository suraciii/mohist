# Self-Review (re-review, round 2)

## Verdict

PASS. Round 1's single must-fix (MF-1, launch coordinator-key derivation) is fixed properly at the planning level, verified against the current codebase. No regressions were introduced by the fix, and none of round 1's eight observations meets the must-fix bar on re-examination.

## MF-1 disposition — FIXED (verified)

**Requirement (round 1):** a deterministic, scope-qualified coordinator key for direct launches (never the raw caller key), defined conflict surfacing for below-layer `LaunchIdempotencyConflictException`, and T-005 acceptance criteria for (a) same key / different agent ⇒ fresh execution, (b) direct key vs product launch key ⇒ no interference, (c) below-layer conflict ⇒ 500, never caller 409.

**Where the fix landed:**

- `design.md` Context: notes the coordinator grain key `(projectId, IdempotencyKey)` has no agent dimension and points at D4's derived key.
- `design.md` D4 drive step: derived key = SHA-256 over `"direct-launch-v1" || projectId || agentId || callerKey` — exactly the launch idempotency scope, no fingerprint or callerKeyId input — tagged with a `\u001f`-delimited prefix and passed as the launcher's `idempotencyKey` argument; byte-exact prompt via `ExactPromptFingerprint = true`; below-layer conflict ⇒ 500 `internal_error`, never a caller 409.
- `design.md` Risks: "Dual idempotency surfaces" bullet rewritten — coordinator *engine* shared (pre-minted adoption, replay-to-same-identities), key space per-surface via the derived key.
- `tasks.json` T-005: description names the derived key + `ExactPromptFingerprint`; three new acceptance criteria covering (a), (b), (c) above.

**Code verification of every load-bearing claim in the fix:**

1. Coordinator key has no agent dimension: `AgentLaunchCoordinatorCodec.KeyFor(projectId, idempotencyKey)` → `agent-launch-coord/{projectId}/{Normalize(key)}` (`Agent/Grains/AgentLaunchCoordinatorTypes.cs:254-255`); `Normalize` only trims (`:324-329`), so a control-char-free derived key passes through unchanged.
2. The launcher keys the grain from the `idempotencyKey` argument and forwards it verbatim into the envelope (`coordinatorKey = …KeyFor(context.ProjectId, idempotencyKey)`; `IdempotencyKey: idempotencyKey`, `Agent/Services/AgentLauncher.cs:267-269,273`) — so passing the derived key keeps grain key, persisted plan `IdempotencyKey`, and replays consistent, exactly as D4 states; the grain itself enforces key↔plan consistency (`InvalidOperationException`, `AgentLaunchCoordinatorGrain.cs:95-98`).
3. `ExactPromptFingerprint` exists and works end-to-end: `AgentLaunchCoordinatorRequest.ExactPromptFingerprint` makes the codec fingerprint use `request.Prompt` byte-exactly instead of trimmed (`AgentLaunchCoordinatorTypes.cs` Fingerprint), and the launcher propagates it (`Prompt: request.ExactPromptFingerprint ? prompt! : trimmedPrompt` plus `Request: request`, `AgentLauncher.cs:280,290`).
4. Conflict is only fingerprint mismatch on the same grain (`AgentLaunchCoordinatorGrain.cs:100-102,193-194`), and the coordinator fingerprint deliberately excludes the volatile definition snapshot (Model/Instructions/Config/StartupContext are not fingerprint input) — so a pending-mapping re-drive cannot spuriously conflict even if the agent's definition changed between drives. D4's "unreachable in correct operation" claim holds: the direct reserve gate (D5) already collapses same-scope+key to one fingerprint before any drive.
5. Surface separation: the product route forwards the raw trimmed caller key (`Api/AgentSessionLaunchRoutes.cs:468-476`) into the same key space, confirming round 1's collision concern was real; the fix's `\u001f` tag makes it structurally impossible — a control character cannot appear in any HTTP header value nor in a direct key (direct surface validates printable ASCII), so no caller-suppliable product key can equal a derived key and no derived key of one agent can equal another's (agentId is hash input). Same key / different agent ⇒ different grain ⇒ fresh execution, matching the write-idempotency spec's launch scope `(projectId, agentId, Idempotency-Key)`.
6. `IAgentLauncher.LaunchIdempotentAsync` already accepts the full `AgentLaunchCoordinatorRequest` plus pre-minted IDs (`AgentLauncher.cs:153-176`), so the composition requires no launcher interface change — the plan is deliverable as specified.

**Justification for the fix's design choices (checked, not just read):**

- Excluding `callerKeyId` from the derived key is correct: the spec's launch mapping scope is `(projectId, agentId, Idempotency-Key)` — not caller-bound — so two callers sharing a key on one agent share the direct mapping and must share the grain; only stop mappings are caller-bound (spec scenario "Stop mappings are caller-bound"), and stop does not go through this grain.
- 500-instead-of-409 for below-layer conflict is consistent with the write-idempotency spec: 409 `idempotency_key_reused` is defined at the direct layer from the direct fingerprint comparison; a coordinator conflict would be an internal invariant violation, not caller key reuse.

## Regression check (fix introduced no new problems)

- **Spec consistency:** D4's new text contradicts no requirement. The derived-key scope mirrors the spec's launch scope verbatim; T-005's new criteria are direct tests of the spec's scope semantics (different scope ⇒ fresh request) rather than new behavior beyond it.
- **Internal consistency:** D4's derived-key paragraph, the rewritten Risks bullet, the Context note, and T-005's description/notes/criteria all state the same mechanism with the same inputs; no artifact now claims the raw caller key reaches the launcher.
- **Coverage / correctness / codebase consistency / task graph:** the fix touched only the launch-drive composition inside D4 and T-005's text; the other dimensions were verified in round 1 and spot-checked unchanged (all six issue ACs still mapped to specs/tasks; seven-route surface, five-state aggregate, 22-key allowlist, projection/cursor contracts, auth ordering, docs-flip-last all unchanged; task graph still acyclic with correct gates).

## Round-1 observations — checked, none meets the must-fix bar

Re-examined adversarially; each remains an observation because tasks.json and/or the specs pin the correct behavior, leaving only design-text looseness or deferred implementation decisions:

1. **D4 unique-index wording vs caller-bound stop** — D4's "unique on `(scopeKind, scopeId, idempotencyKey)`" read literally would collide a second caller's same-key stop row; T-006's criterion and the spec's caller-bound scenario pin the correct behavior (fresh keyed stop, no cross-caller replay). Design wording only; suggest stating that the stop unique key includes `callerKeyId`.
2. **401/403 envelope shape on `/api/v1`** — existing middleware emits product-shaped bodies; D8/T-003 pin the direct `{"error":{code,message}}` envelope. Where the translation happens is an implementation decision; the contract is pinned.
3. **Rebuild/generation-swap machinery deferred** — v1 needs generation-one stability only; covered by T-004/T-007 criteria.
4. **Open questions left open** (watermark granularity, `session.unknown` cadence, backfill throttle, retention default, rate limiting) — observable contracts pinned; these are implementation decisions.
5. **Follow-up pre-minting decided in T-005 but "open" in design** — T-005's notes commit to extending `AgentSessionFollowupReservation`; fold into design text when implementing.
6. **No control-plane Session deletion exists today** — T-007 criterion 5's tombstone path can only be exercised via simulated deletion facts until such an action ships; stream-side behavior is still correctly specified.
7. **Issue comments (2026-08-10/13)** — worktree/artifact evidence handling and build-supervision records; no overlap with this plan.
8. **Naming drift** (`MarkTurnTerminal` → `MarkTurnTerminalAsync`; `packages/cli` → `packages/cli/Mohist.Cli`) — cosmetic; tasks reference the right members.

## New observations (this round)

1. **Derived-key string layout is slightly under-specified.** D4 says "SHA-256 over `direct-launch-v1 || projectId || agentId || callerKey` … tagged with a `\u001f`-delimited prefix". The collision-impossibility argument requires the `\u001f` delimiter to be part of the *final key string* (e.g., `direct-launch-v1\u001f{hash}`), not a hash-only digest: a hash-only key is 64 hex characters that a product caller could in principle replay literally. T-005 criterion 7 tests "identical key string", which would not catch a hash-only implementation (an ordinary test key never equals a 64-hex digest). The design text does say the tag is attached, and the argument in both design and T-005 notes depends on it, so a faithful implementation is safe; recommend the implementer make the layout explicit (tag + delimiter + hash) and consider a criterion that a caller-supplied 64-hex key cannot equal any derived key.
2. **Direct pre-mint derivation should not reuse the product route's.** The product route pre-mints IDs from `"{projectId}\n{idempotencyKey}"` (`AgentSessionLaunchRoutes.cs:105-107`); D4 pre-mints at reserve but does not specify the derivation. Any distinct derivation is fine (IDs are opaque tokens); noting so the implementer does not accidentally converge on the product inputs for the same raw key string.

## Summary

MF-1 fixed and verified in code; no other must-fix findings; round-1 observations correctly remain observations.

<promise>PASS</promise>
