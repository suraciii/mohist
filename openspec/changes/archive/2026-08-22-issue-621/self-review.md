# Issue 621 Plan Self-Review

Review round: second review (re-review). I re-read the canonical issue with `mo issue view 621 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the updated artifacts. The issue body supplies the Product Shape, four acceptance criteria, and the Server/liveness non-goals.

## Verdict

PASS. No must-fix problem remains; the plan is ready to build.

## Previous Findings Disposition

- F-001, Runner-only boundary: fixed. `proposal.md:20-23` and `design.md:29-33,65` explicitly keep unpublished detection in the Runner, remove Server/outbox queries, and preserve the existing Server reply action. `tasks.json:T-001` explicitly forbids a `ServerConnection` probe, endpoint, `SlackOutboxStore` query, delivery lookup, persistence schema, and Server test dependency.
- F-002, attempted action versus successful publication: fixed. `design.md:59-67` makes the Runner-local normalized `tool_call.started` observation authoritative and records the attempt before completion. `spec.md:1-6` and `tasks.json:T-001/T-002` explicitly cover accepted, rejected, non-zero, and interrupted sends, while excluding final text, unrelated tools, liveness, outbox, and provider state.
- F-003, reminder budget: fixed. `proposal.md:9-10`, `design.md:47-53`, `spec.md:41`, and `tasks.json:T-001` define `DEFAULT_REPLY_GUARD_REMINDER_BUDGET` as two, increment the count before each advisory, and test exhaustion without a third opportunity.
- F-004, Pi follow-up admission versus terminal completion: fixed. `design.md:95-105`, `spec.md:97-112`, and `tasks.json:T-003` distinguish SignalR acknowledgement, Pi `preflight(true)`, and Pi `steer` from actual model completion. The revised plan requires completion handling for both Pi branches and tests the streaming case against the current `PiRuntime.followup` behavior.

## Dimension Review

### Issue Goals And Acceptance Criteria

Checked, no issue. The plan covers every criterion:

- A valid Slack execution context and reply anchor make the initial and follow-up turns eligible. The shared coordinator evaluates them at the actual terminal boundary and the advisory explicitly permits deliberate silence (`spec.md:1-25,40-58`).
- The reminder budget is finite and defaults to two. State is claimed before asynchronous advisory work, and duplicate or late terminal signals cannot open another opportunity (`design.md:41-55,107-111`; `spec.md:61-75`).
- An existing reply action invocation suppresses the guard immediately, including a send that later fails or is interrupted (`design.md:59-67`; `spec.md:26-38,121-123`).
- Turns without valid Slack context bypass the guard and retain their existing behavior (`design.md:18-25`; `spec.md:114-126`).

The non-goals are also covered: no Server-side unpublished detection or fallback reply is planned, and the original outcome, terminal activity, and liveness sequence remain unchanged apart from a bounded best-effort wait (`proposal.md:11-12,20-23`; `design.md:85-93`).

### Coverage

Checked, no issue. The spec has requirements for eligibility and action observation, advisory behavior, loop prevention, failure/interruption preservation, actual follow-up completion, and non-Slack/liveness independence. T-001 covers the shared predicate and coordinator cases; T-002 covers initial Pi/OpenCode AgentJob turns; T-003 covers OpenCode follow-ups plus Pi idle and streaming follow-ups. The task acceptance criteria include rejected sends, explicit silence, default-two exhaustion, duplicate signals, timeout/failure/unavailable/interrupted paths, and unchanged reporting.

### Correctness

Checked, no issue. The approach uses the only local fact that matches the issue's contract: the reply action invocation starts. It does not substitute successful delivery, assistant text, or liveness for an attempt. Advisory calls reuse the current runtime session and Slack context, preserve Agent-owned content, stop on an attempt, and retain the original execution result on all guard failures. Separating follow-up admission from terminal completion prevents the known Pi `steer` and `preflight` early-return behavior from causing premature advisories or terminal activity.

### Current Codebase And Conventions

Checked, no issue. The plan follows the existing split between the Runner orchestration layer and the Pi/OpenCode deep runtime modules, reuses `SlackExecutionContext`, `inlineSlackCollaborationSkill`, `buildExecutionEnvelope`, normalized runtime observers, and the existing follow-up handler/outbox. It explicitly accounts for the current `PiRuntime.followup` branches in `packages/runner/src/runtime/pi/runtime.ts` and the fire-and-forget completion flow in `packages/runner/src/server/followup-handler.ts`. The proposed runtime completion and abort extensions are internal; no new Server wire contract or persistence schema is introduced.

### Task Breakdown And Verifiability

Checked, no issue. `tasks.json` parses, its dependency graph is acyclic and ordered `T-001 -> T-002 -> T-003`, and all spec anchors resolve to requirements in `specs/runner-reply-guard/spec.md`. The ordering establishes the shared state and observation contract before initial-turn integration, then applies the same coordinator to follow-up terminal handling. Each task has concrete acceptance criteria and focused Runner typecheck/test-suite verification.

## Observations

- `design.md:61-63` leaves the exact normalized input forms for `mo slack message send` as an implementation open question. The current runtime projections expose tool names and raw inputs, and the plan requires tests for both Pi and OpenCode shapes; implementation should verify the actual shell-command representation rather than match only a literal tool name. This is not a must-fix plan defect because the task acceptance criteria already make the predicate and its tests explicit.
- `design.md:89-93` intentionally delays follow-up terminal activity until bounded advisory processing completes. The final status and payload remain unchanged, satisfying the issue's liveness semantic, but the implementation should keep the delay within the stated finite bound and preserve exactly-once activity emission.
- `design.md:107-111` scopes guard state to the active Runner operation and explicitly excludes cross-restart reminder persistence. That is consistent with the issue scope; reconciliation must continue to require a live terminal operation and fresh local observations as specified.

No product files or plan artifacts were modified. Only this review artifact was updated.

<promise>PASS</promise>