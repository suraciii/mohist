# Self-Review

## Verdict

FAIL. One must-fix planning defect: the direct launch idempotency scope `(projectId, agentId, Idempotency-Key)` cannot be delivered by the composition the plan specifies, because the only launch engine it composes is keyed `(projectId, Idempotency-Key)`. Everything else — coverage of all six acceptance criteria, the projection/cursor/stop contracts, the task graph — is sound and verified against the codebase.

## Must-Fix Findings

### MF-1 — Launch drive step has no coordinator-key derivation; the natural implementation breaks the plan's own launch idempotency scope

**Where:** `design.md` D4 (drive step) and D4 risk note "identity rules stay shared"; `tasks.json` T-005.

**Evidence (current code):**
- `AgentLaunchCoordinatorCodec.KeyFor(projectId, idempotencyKey)` → `agent-launch-coord/{projectId}/{key}` (`packages/server/src/Mohist.Server/Agent/Grains/AgentLaunchCoordinatorTypes.cs:254`). The coordinator grain identity has **no agent dimension**.
- `AgentLauncher.LaunchIdempotentCoreAsync` keys the coordinator with `KeyFor(context.ProjectId, idempotencyKey)` (`packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:267-269`) and the product route forwards the raw `Idempotency-Key` header verbatim (`packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:95-166`).
- A different fingerprint on the same coordinator grain key raises `LaunchIdempotencyConflictException` (`AgentLaunchCoordinatorGrain.ResumeAsync`).

**Failure case (constructible from the plan's own contract):** the write-idempotency spec pins the direct launch scope as `(projectId, agentId, Idempotency-Key)` — a key reused on a *different agent* is a fresh request in a distinct scope, not a conflict. D4's drive step says only "launch goes through `IAgentLauncher.LaunchIdempotentAsync`", whose only key parameter feeds the `(projectId, key)` grain. The natural implementation forwards the caller's raw key, so:

1. Launch agent A in project P with key K → succeeds.
2. Launch agent B in project P with the same key K → the direct layer reserves a fresh mapping (different `agentId` scope, pre-minted IDs), then the drive step lands on the *same* coordinator grain `agent-launch-coord/P/K`, whose stored fingerprint differs → `LaunchIdempotencyConflictException`. The request the spec defines as fresh either errors, is mis-served as `409 idempotency_key_reused`, or leaves the reserved mapping permanently `pending` (every replay re-throws).
3. Independently, a direct caller's key can collide with a *product-route* launch using the same key string in the same project, since both surfaces feed raw keys into one grain key space. D4's risk note "launch delegation funnels through the same coordinator, so identity rules stay shared" is precisely the wrong sharing: the coordinator's identity rule is narrower than the direct contract's.

**Violates:** issue AC "重复、乱序、无效续读和终态等情况都有明确、可理解的结果" (a well-formed fresh request yields a stuck/confused result, not a clear one) and the plan's own spec requirement "The first request durably maps key and fingerprint to its canonical outcome … whose idempotency scope is `(projectId, agentId, Idempotency-Key)` for launch", whose complement (different scope ⇒ fresh request) the composition cannot deliver. It also undermines AC "同一请求的重试返回原执行，而不是创建重复执行" by making scope identity depend on a key space the direct contract does not control.

**Fix direction (planning-level):** specify a deterministic, scope-qualified coordinator key for direct launches (e.g., a direct-surface discriminator + agentId + caller key, hashed like `KeyFor`'s normalization), never the raw caller key, so direct scopes cannot collide across agents nor with product-surface launches; state how `LaunchIdempotencyConflictException` from below the direct layer is (not) surfaced; add T-005 acceptance criteria for (a) same key, different agent ⇒ fresh execution with no 409, and (b) direct key colliding with a product launch key ⇒ no interference.

## Issue Acceptance Coverage

**Checked — all six criteria addressed; no gap besides MF-1.**

- 未认证或无权调用的请求不会启动 Agent 执行 → auth spec (PAT grant model, `ExternalAgentCaller`, authorization-before-lookup, zero side effects on 401/403) + T-001/T-002.
- 同一请求的重试返回原执行 → write-idempotency spec + T-005/T-006 (reserve/pre-mint/drive/finalize, durable rejections, caller-bound stop). Covered except the MF-1 scope-composition hole.
- 客户端可以知道已接受、排队、执行中、已结束或状态未知 → five-state aggregate with fixed precedence + T-003/T-008; the 22-key allowlist count matches the spec exactly.
- 网络中断后从原位置继续读取 → event-resume spec + T-007 (opaque cursors, exclusive-after, generations, retention floor, tombstones) and Job-anchored recovery reads (T-008).
- 外部只看到面向使用者的公开结果 → strict allowlist DTO with dedicated serializer and no-internal-leak tests (T-003), projection-served reads/events (T-004), docs flip (T-009).
- 重复、乱序、无效续读、终态都有明确结果 → `409 idempotency_key_reused`, `409 stop_outcome_unknown`, `400 cursor_invalid`, `410 cursor_expired`, `503 projection_lag`, terminal fence, documented dedup rules (T-005/T-006/T-007/T-009).
- Non-goals respected: no Slack UX, no Runner/Runtime Session exposure (seven-route enumeration test, T-008), no Agent config/Workspace/Workflow redesign; no second lifecycle/queue/event bus anywhere in the design.

## Correctness

**Checked — one must-fix (MF-1); otherwise the approach satisfies the criteria.**

- Reserve → pre-mint → drive → finalize is crash-safe for the cases the design claims: pre-minted launch IDs are adopted verbatim today (`IAgentLauncher.LaunchIdempotentAsync` `preMintedSessionId/InputId/TurnId` params); follow-up re-drive dedups via `AcceptFollowupCommand.IdempotencyKey` → `FindFollowupInputByIdempotencyKey` once committed, and the pre-mint extension covers the reserve-before-commit crash window (the design's recommendation to extend `AgentSessionFollowupReservation` is the right call; see Observations).
- The direct fingerprint gate is justified: `AgentLauncher` trims the prompt (`AgentLauncher.cs:77`), so the launcher fingerprint cannot enforce the direct contract's byte-exact text identity; the versioned server-side canonical form (D5) is the correct fix.
- Keyed stop composes the real lifecycle: `StopQueuedTurnAsync`, `ClaimTurnStopAsync(turnId, operationId?)`, `MarkTurnStopDispatchedAsync`, `CompleteTurnStopAsync`, `StopOperationDeadline`, `MarkTurnTerminalAsync`, and the stop-recovery reminder all exist as described; unresolved stop blocks follow-up admission (`PendingStop` → `StopOperationInProgressException` in `BeginFollowupAsync`), matching "admission stays blocked".
- Durable admission rejections are feasible: coordinator plans persist `RejectionReason` durably and replays rethrow; `AgentLaunchVisibility.Rejected` gives the projector a durable canonical fact — the 200 `terminal/rejected` replay-forever contract is implementable as specified.
- The projection design (one EF transaction for snapshot + journal + stream allocation + checkpoint; single background projector woken by `EventPushQueue` with a durable poll loop) is consistent with the SQLite single-writer constraint and with the existing `AgentSessionEventRow`/`AgentJobEventRow` outbox tables.
- Cursor design is sound: `ISecretStore` infrastructure exists (`Infrastructure/Security/Secrets/`), so the HMAC keyring with key-id rotation is not a fabrication; tombstone/floor/generation semantics match the spec scenario-by-scenario.

## Current-Code Consistency

**Checked — every load-bearing Context claim verified; one inconsistency is must-fix (MF-1).**

- `AuthResolutionMiddleware` first-hit Bearer-then-`mohist_session` order, uniform 401 with Bearer challenge: confirmed (`Auth/Identity/AuthResolutionMiddleware.cs`); no `BearerOnlySurface` yet (to add, T-002); `AuthExemptionList` exempts nothing under `/api/v1`.
- `CredentialRow` has scopes/TTL/revocation and a single nullable `ProjectId` (integration binding) and no grant model: confirmed — D2's "leave `ProjectId` untouched, add kind + child table" is the right non-backfill split, and absent-kind denial reproduces pre-existing-PAT behavior as the spec requires.
- `mo auth token create` posts `{name, scope, ttlHours}` to `POST /api/auth/tokens` with no grant options: confirmed (`MohistCliCommands.Auth.cs`, `AuthTokenRoutes` + `PatPolicy`) — T-001's extension points are real.
- `RouteScopeRequirement`/`RequireScopes` conventions exist and match the planned read/write scope split; the middleware's method-based defaults align (GET → operator-or-readonly, POST → operator).
- `ProjectRefResolver`, EF Core additive-migration convention, hosted-service hosting, and `EventPushQueue` all exist as referenced; `docs/agent-api.md` is `wip-not-implemented` and both design docs carry the target-only Status sections the docs flip (T-009) will update.
- Spec/task anchor format and tasks.json structure match the repo's established openspec conventions (cf. issue-589); the task graph is acyclic, priority-ordered, and correctly gates the docs flip last.

## Task Breakdown

**Checked, no issue.** T-001 → T-002 (grant before caller resolution) and T-003 → T-004 (DTO before projection) are the right orderings; T-005/T-007 correctly wait on both the auth pipeline and the projection; T-006 builds on T-005's mapping infrastructure; T-008's placement of the route-enumeration test is justified because it completes the seven-route surface; T-009 depends on everything. Every task carries concrete, testable acceptance criteria (identity-idempotent replay, kill-between-transactions, tamper/generation/tombstone, allowlist key-set, zero-side-effect assertions per store). `mode`/`type`/`output` fields are consistent.

## Observations

1. **D4's unique-index wording contradicts caller-bound stop if read literally.** "unique on `(scopeKind, scopeId, idempotencyKey)`" with stop scope `(turnId)` would make a second caller's same-key row collide and replay the first caller's mapping. T-006's acceptance criteria pin the correct behavior ("does not resolve the first caller's mapping — evaluated as a fresh keyed stop"), so the plan as a whole is right, but the design should state that the stop unique key includes `callerKeyId`.
2. **401/403 envelope on `/api/v1` is unresolved between D1/D8.** The existing middleware emits product-shaped bodies (`code:"unauthorized"`, `Insufficient scope…` with principal/scope details) while D8 and the error table pin the direct envelope `{"error":{"code":"unauthenticated"|"forbidden",…}}`. Decide where scope/grant failures are emitted on this surface so the direct envelope (and the `unauthenticated` code the auth spec names) actually applies.
3. **Rebuild/generation-swap machinery is deferred** (operator-triggered, future) while the event-resume spec states rebuild requirements with scenarios. V1 only needs generation-one stability, restart/replay stability, and old-generation-cursor 400s — all covered by T-004/T-007 — but the rebuild scenarios remain unimplemented until that future work lands; acceptable, worth tracking.
4. **Open questions left open at plan stage:** watermark granularity (per-source-table max vs per-stream), `session.unknown` emission cadence, backfill throttle/metrics, cursor-retention window default, and rate limiting. The observable contracts (503-vs-unknown, at-least-one unknown component fact, no compaction in v1) are pinned, so these are implementation decisions, not plan holes.
5. **Follow-up pre-minting is decided in T-005 but still "open" in the design.** T-005's description and notes commit to extending `AgentSessionFollowupReservation` to adopt pre-minted Input/Turn IDs; fold that decision into the design text when implementing so the artifacts don't disagree.
6. **No control-plane Session deletion exists in the code today**, so the closed-stream tombstone path (T-007 criterion 5) can only be exercised by simulated deletion facts until such an action ships. The spec requires the stream-side behavior regardless; flagging so it isn't mistaken for dead code.
7. **Issue comments (2026-08-10/13) do not constrain this plan.** The "product decision" comment concerns worktree/artifact evidence handling and the T-007.8 build supervision records of the planning workflow itself; nothing in the external-agent API plan touches or contradicts it.
8. **Minor naming drift in the design Context:** `MarkTurnTerminal` → actual `MarkTurnTerminalAsync`; CLI path is `packages/cli/Mohist.Cli` (proposal says `packages/cli`). Cosmetic; tasks reference the right members.

<promise>FAIL</promise>
