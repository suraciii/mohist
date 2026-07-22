# Self-Review — Issue 463

Reviewing `proposal.md`, `design.md`, `tasks.json`, and `specs/` against the issue.

## Verified correct (no change needed)

- **D1 delivery premise holds.** Transcript events ARE filtered by the per-connection subscription set (`SignalRTranscriptEventPublisher.cs:49` → `ShouldNotify`). Adding `session.followup_completed`/`session.followup_failed` to the web canonical set unblocks delivery. T-001 is sound.
- **T-002 upload premise holds.** Pi runtime events reach the server transcript: collected into `events` (`pi.ts:122`) and reported via `reportWithTerminalSignal` (`pi.ts:147`); the agent-job path uploads per-event (`agent-job-executor.ts:136-153`). So a Pi `model.resolved` will be accumulated and surface through the summary. (Note for implementers: Pi reports turn facts at turn end, not streamed — acceptable, not a regression.)
- **D3 rationale holds.** The follow-up input is enqueued before the terminal in every path (`followup-handler.ts:91` before `:106`), so refreshing `LastDataAt` on the input would leave the session active after `session.followup_failed` and break the recovery invariant (`AgentSessionRecoveryGrainSpecs.cs:322`). The web-side reconciliation is the correct call.
- **Coverage.** All three issue acceptance criteria map to tasks (T-001, T-002, T-003). Issue Non-Goals (no followup-flow refactor, no change to the terminal local-fallback) are respected. `tasks.json` is valid JSON with an acyclic, strictly-lower-priority dependency graph.

## Problems that must be fixed

### P1 — Activity-state spec scenario contradicts the chosen design (spec ↔ design inconsistency)

`specs/agent-session-followup-activity-state/spec.md`, Requirement 1, Scenario 1 reads:

> **WHEN** the web presents a follow-up user input as a new round
> **THEN** the session's reported status SHALL be active

Read literally ("new round" = any rendered follow-up round), this demands the session be `active` whenever a follow-up round is rendered. But `design.md` D3 deliberately renders the follow-up prompt as a round while the session is inactive, gating only the active/thinking indicator on runtime response events. So the scenario, read literally, contradicts D3 — and the only way to satisfy it would be to refresh server activity on the follow-up input, which D3 rejects because it breaks the recovery invariant.

The requirement body text uses "new **active** round" consistently; Scenario 1 dropped "active". An implementer or test author reading Scenario 1 literally would write `render follow-up round ⇒ assert status active`, which cannot pass under D3.

**Fix:** tighten Scenario 1 to "presents a follow-up user input as a new **active** round" (matching the requirement body and D3), so the spec unambiguously expresses: the active/thinking presentation — not the mere rendering of the prompt — is what must agree with the active status.

### P2 — Residual web field-name inconsistency for `model.resolved` (scope gap on symptom #2)

The issue frames symptom #2 as a **three-layer field-naming** inconsistency ("字段在 runner、server、web 三层命名不一致"). The plan unifies the runner (Pi) and server to `resolvedModel` (D2), and the web *summary/read model* already reads `resolvedModel`. However the web's **live** `model.resolved` event type still declares the wrong field:

- `packages/web/src/entities/agent/model/types.ts:105` → `'model.resolved': SessionRuntimeBase & { model: string }`

The runner emits `resolvedModel` (`event-projection.ts:114,264`), so this web event type is inconsistent with what the runner sends and with the rest of the web. There is no active live consumer today (no `onAgentEvent('model.resolved')` handler), so it is latent rather than user-visible right now — but it is precisely the runner↔web naming inconsistency the issue calls out, and the proposal's capability statement promises "one consistent type and field across … web."

The plan never mentions this type and even asserts "OpenCode path is unchanged," leaving the inconsistency in place.

**Fix:** either align the web `model.resolved` event type to `resolvedModel` (recommended — it is the issue's stated concern and the runner already sends `resolvedModel`), or explicitly scope it out in `design.md`/`tasks.json` with rationale. As written, the capability under-delivers on its own end-to-end consistency claim.

## Verdict

P1 is a spec↔design contract defect with real implementation-misdirection risk; P2 is a scope gap on one of the issue's three named symptoms. Both are concrete and fixable before building.

<promise>FAIL</promise>
