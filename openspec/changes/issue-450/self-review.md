# Self-Review - Issue #450 Pi Workflow Path

Scope: current issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against `docs/actions/pi.md`, `design/runtimes/pi.md`, repository architecture/testing rules, and current Runner/Server seams. This review modifies no other file.

## Findings

### F-1 Critical: Action-stream ownership is asserted from an unauthenticated caller value

The plan returns every current stream owned by a requested `runnerId`, including its physical binding, work directory, stream ID, cursor, checkpoint, and lifecycle (`design.md:106-108`; `specs/pi-workflow-session/spec.md:150-156`; `tasks.json:69-70`). Event writes and rebinds then treat equality between another caller-supplied `runnerId` and the stored binding owner as the ownership check (`design.md:92-100,178`; `tasks.json:72-76`). At the same time, the plan explicitly adds no Runner authentication and says the stream ID is not an authorization capability (`proposal.md:27`; `design.md:56,108`).

Current Runner routes derive identity solely from `/api/runner/{runnerId}` and `ServerConnection` merely inserts its configured string into that URL (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:21-35`; `packages/runner/src/server/connection.ts:320-327`). Any caller can therefore request another Runner's inventory, copy the returned owner and stream data, append the next sequence, or attest the rebind fence. The proposed non-owner tests compare caller-controlled strings and cannot prove ownership. This contradicts the mandatory ownership-proof stage and stale-report rejection rule in `design/architecture.md:50-71`. Define an unforgeable Runner principal or binding-owner proof covering inventory, Action events, and rebind; if inventory remains pre-registration, the proof must exist before registration rather than being established by the same unauthenticated `runnerId` claim.

### F-2 High: Post-turn transport failure makes T-006's checkpoint requirement impossible

The protocol defines `closed` as Server-applied state: `turn.reporting-complete` must be acknowledged before local closure (`design.md:174`; `tasks.json:106`). The Action contract correctly allows a completed turn to return success when all facts are durable locally but the bounded post-turn Server delivery fails, leaving background delivery to finish later (`design.md:184`; `specs/pi-workflow-action/spec.md:122-126`). T-006 nevertheless requires the reporter to project the required facts and "close the turn checkpoint before Action return" while preserving success on that same transport failure (`tasks.json:167`). With the Server unreachable, both conditions cannot hold. Split locally durable pending closure from Server-acknowledged `closed`, and make the acceptance criteria use the same completion point.

### F-3 High: Removing the historical Reset fallback contradicts the canonical runtime design

D5 and T-003 require removing Reset's fallback for an unregistered historical runtime (`design.md:102`; `specs/pi-workflow-session/spec.md:93`; `tasks.json:78,81`). The canonical Pi design explicitly requires that fallback to remain unchanged (`design/runtimes/pi.md:352-357`), and current code uses it to make an otherwise unregistered old binding recoverable through OpenCode Reset (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:290-300`). Pi command admission must reject an observed Pi binding before reservation/dispatch, but that does not require regressing Reset for unrelated historical runtime values. Preserve the existing fallback and scope the new capability predicate to registered-but-not-command-capable Pi bindings.

### F-4 Medium: Pi cost facts are dropped despite an existing canonical usage surface

The canonical Pi event map includes `cost` with token usage (`design/runtimes/pi.md:218-230`). Existing Session state, API, and Web already support `costAmount` and `costCurrency` (`packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs:6-18`; `packages/runner/src/runtime/opencode/event-projection.ts:123-126`). The proposed Pi projector, required-fact set, outbox completion, and Web fixture enumerate token dimensions but omit cost (`design.md:153-160,184`; `tasks.json:43,108,167,198`). A Pi turn can therefore satisfy the plan while silently dropping a canonical audit/usage fact. Include cost and currency in projection, durable reconciliation/checkpoint completeness, and end-to-end tests, or explicitly reconcile the canonical runtime contract before implementation.

### F-5 Medium: T-007 requires reporting diagnostics that no planned Session contract carries

T-007 requires a Pi Workflow Session fixture to render "reporting diagnostics" (`tasks.json:198`). The plan places outbox/readiness diagnostics in Runner diagnostics/logging and keeps runtime diagnostics out of Action output (`design.md:164,186`; `specs/pi-runtime/spec.md:41,55-59`), but defines no AgentSession event, transcript part, persisted field, API DTO, or Web adapter for a reporting diagnostic. Current Session read models expose failure reason, transcript, event summary, usage, and lineage, not a diagnostic collection (`packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs:6-54,161-182`). Remove that rendering requirement if diagnostics remain operator/task-log data, or define the missing Session persistence/API/UI contract and assign its implementation.

### F-6 Medium: The unattended tool-execution contract has no verification owner

The product/runtime authorities require Pi tools to execute without per-tool confirmation (`docs/actions/pi.md:128-130,158-165`; `design/runtimes/pi.md:317-322`). The OpenSpec design instead lists per-tool approval as a non-goal (`design.md:31`) and tests tool event projection, but no requirement, SDK smoke assertion, runtime acceptance criterion, or fake-backed workflow test proves a tool call cannot enter a confirmation-blocked state (`design.md:46-50,202-210`; `tasks.json:13-16,39-48,174`). Add this fixed Runner configuration/behavior to the smoke and PiRuntime tests so the core unattended-execution promise cannot be omitted during SDK integration.

### F-7 Medium: Task-only rejection introduces an unsupported Action restriction

The issue asks for the Workflow-direct `mohist/pi` Action and says its behavior remains like `mohist/opencode`; its non-goals do not exclude check use. The canonical Action contract says checks reuse the same Action contract and the Action does not know whether it is hosted by a task or check (`design/workflow/actions.md:222-225`). The plan newly declares Pi task-only and mandates both Server and Runner rejection (`proposal.md:7,14`; `specs/pi-workflow-action/spec.md:1-22`; `tasks.json:133-140,160`). This is a product restriction invented by the implementation plan, not a documented implementation gap with an owner. Either support checks through the canonical Action boundary, or first update the governing product/design spec and explicitly assign the temporary gap to a follow-up issue.

## Structural Checks

- `tasks.json` parses; all seven task IDs and dependencies resolve and the graph is acyclic.
- Every referenced spec file and requirement anchor resolves.
- The issue's seven explicit acceptance criteria are represented, and AgentJob execution, Pi Session-command routing, catalog/UI selection, ACP/RPC, and a generic `AgentRuntime` remain excluded.
- The Runner-scoped stream inventory now closes the prior missing-manifest discovery gap, but its authorization contract is not safe to build as written.

## Required Repair Boundary

Do not solve F-1 by adding Runner enrollment or broad transport authentication to issue #450. The governing Pi design does not require an Action-stream cursor, durable Runner outbox, startup inventory, projector checkpoint, or post-crash replay of Session facts: Pi events are in-process callbacks, final state is reconciled from `session.messages`, and crash-window redelivery may duplicate a turn (`design/runtimes/pi.md:172-193,207-227`). Remove that invented protocol from the proposal, design, specs, and tasks. Report Pi facts through the existing binding-validated AgentSession event path; if required reporting cannot complete, fail the task with the existing reporting error instead of promising success with pending delivery.

Keep only the minimum concurrency mechanism required by the product contract: one process-local coordinator keyed by logical Workflow Session, acquired by both task and check hosts around the complete Action turn (including cleanup where applicable). It must serialize OpenCode and Pi use of the same logical Session, but it must not absorb Session commands, durable recovery, authentication, or runtime-specific state.

While simplifying, resolve F-2 through F-7 directly: use one reporting completion point; preserve the historical unregistered-runtime Reset fallback while rejecting registered-but-command-unavailable Pi; include cost/currency with all token usage; remove unmodeled Session-page reporting diagnostics; verify unattended tool execution in the SDK smoke and PiRuntime tests; and support `mohist/pi` through the existing check Action contract instead of inventing task-only rejection.

## Verdict

The core Pi vertical slice is comprehensively decomposed, but the ownership protocol, contradictory closure criterion, canonical Reset regression, and uncovered contract points above must be resolved before implementation.

<promise>FAIL</promise>
