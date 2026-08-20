# Self-Review: Issue 618

Review mode: first review. I read the live issue with `mo issue view 618 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the artifacts. The review basis is the issue's User Voice, Product Shape, Domain Model, eight Acceptance Criteria, and Non-Goals.

**Verdict: FAIL**

## Must-Fix Findings

### 1. The capability credential transport is not defined at a boundary that can satisfy the secrecy criteria

**Violates:** Issue Acceptance Criteria 4 and 5. The credential must be execution-scoped, absent from prompts, transcripts, logs, Session state, and durable records; restart, recovery, and expiry must cause reauthorization with a newly issued credential.

The design says the credential will use an "ephemeral Server-to-Runner execution field" and will be injected at the Manager CLI process boundary (`design.md:84-90`), but the design leaves the exact Pi/OpenCode process boundary as an open question (`design.md:142-145`). T-003 repeats the desired behavior and asks for process-only injection tests (`tasks.json:50-62`) without defining the transport and process mechanism that those tests would verify.

This is a concrete gap in the current execution contracts, not only an implementation detail. Initial Manager execution is built as a `WorkDispatch` (`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Dispatch.cs:13-110`), and the dispatch is serialized and stored for active/recovery delivery (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:552-618`). A credential added to the persisted dispatch or `WorkDispatch.with` would violate the runtime-only requirement. A credential added only to the follow-up control message would miss initial and recovered executions. The Runner currently builds the model-facing envelope from the dispatch and generic command execution inherits the Runner environment (`packages/runner/src/runtime/agent-job-executor.ts` and `packages/runner/src/system/process.ts:94-125`), so the plan must identify how `mo` receives the value without making it available to generic model shell commands or the runtime process.

Before build, the plan must choose and specify a non-durable carrier for initial, follow-up, and recovered executions, its invalidation and cleanup behavior, the exact `mo` invocation boundary for both Pi and OpenCode, and the separate authentication path for the reply action. It must also state how the value is redacted before every model-facing and durable output path. Without that decision, the credential acceptance criteria cannot be implemented or meaningfully verified.

### 2. The plan does not define a working Manager reply route and outbox ownership contract

**Violates:** Issue Acceptance Criteria 1, 2, and 3. The final result must be posted by the Manager Agent through the reply action, Server-authored Manager replies must disappear, and the reply must not create a Manager input loop.

The current reply action is routed only through the project Slack Connection group (`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:386-419`). It calls `SlackOutboxStore.EnqueueAgentReplyAsync`, which resolves a conversation mapping but then validates liveness against `AgentConnections` and searches for Connection-owned outbox rows (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackOutboxStore.cs:352-380`). Manager ingress instead uses the synthetic Manager project and stores the Manager DM mapping/outbox under the enrollment id and `SlackDeliveryOwnerKinds.Manager` (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerIngressService.cs:72-82`). Therefore, the existing `mo slack message send` path cannot simply be reused for a Manager Session: it can resolve a Manager mapping but then fails the Connection-owned liveness/ownership checks.

The design asserts that the reply route will validate the anchor and preserve ownership (`design.md:56-64`) and that a Manager owner remains available for Manager DM rows (`design.md:104-108`), but neither T-001 nor T-004 defines the concrete route, authentication principal, Manager owner selection, full-anchor validation, or promotion/deduplication behavior needed to make that assertion true. The task acceptance text only states that the action is the sole source of reply text (`tasks.json:11-16`); it does not cover the Manager-specific route/owner boundary.

The plan must explicitly define how an anchored Manager reply selects `ManagerProjectId` and `OwnerKind.Manager`, validates workspace/conversation/thread/session/dispatch against the immutable origin, and promotes or deduplicates the same Manager progress row. It must cover initial turns, follow-ups, no-progress fast completion, duplicate sends, and bot-event loop prevention in tests. Until that contract is present, the stated sole-reply-owner flow cannot deliver the issue's required Slack result.

## Dimension Checks

- Issue goals and acceptance criteria: re-read from the live issue before the artifacts; the two gaps above are relative to the issue, not just to the plan's framing.
- Coverage: incomplete for the execution-only credential boundary and Manager-owned reply path; the remaining acceptance criteria have corresponding capability specs and task acceptance statements.
- Correctness: the ordinary-session, retired-protocol removal, allowlist, authorization-recheck, and shared-liveness directions are coherent in isolation, but the two missing boundaries prevent the end-to-end flow from satisfying the issue.
- Consistency with the current codebase: the planned natural-language/session changes align with the existing Session and Slack execution-context contracts; the credential and Manager reply assertions do not yet align with the current durable dispatch and outbox-owner contracts described above.
- Task breakdown, ordering, and verifiability: the dependency order is generally sensible, but T-003 has no selected credential carrier/process boundary and T-001/T-004 have no concrete Manager reply route contract. The named tests cannot establish the required security and delivery properties until those boundaries are specified.

## Observations

- `design.md:142-147` leaves TTL/clock skew, multi-Server lease-store topology, reply-action credential choice, owner claim/transfer behavior, one-time-code delivery, and adapter reaction capability gating open. These are not additional must-fix findings in this review where the issue does not prescribe the exact value, but each should be resolved before implementation of its affected capability.
- The allowlist is described as one catalog, but its exact logical operation ids, argument schemas, and route mapping remain partly expressed as "where applicable" (`design.md:68-78`, `tasks.json:30-44`). Tests should assert the canonical catalog directly so the old `SlackManagerAgentTools` set cannot drift from CLI and Server enforcement.
- The migration keeps the retired parser/fence schema available during a rollback window (`design.md:133-138`). That can be an operational rollback measure, but the deployed new path must not retain a compatibility caller or interpret late `mohistManagerTool` output, consistent with the issue Non-Goals.

<promise>FAIL</promise>