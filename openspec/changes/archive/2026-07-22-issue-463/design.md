## Context

Issue 463 fixes three independent cross-layer inconsistencies around follow-up. The same logical fact takes different shapes in runner, server, and web, so the UI shows wrong or missing state. Current behavior, verified in code:

1. **Follow-up terminals vanish.** The runner emits `session.followup_completed` / `session.followup_failed` (`packages/runner/src/server/followup-handler.ts:178`); the server recognizes them (`packages/server/src/Mohist.Server/Sessions/Services/TranscriptEventTypes.cs:8-9`) but the web's canonical transcript subscription set omits both (`packages/web/src/shared/lib/canonical-event-types.ts:52-67`). Delivery is gated by that same per-connection set (`SignalRTranscriptEventPublisher.cs:49`, `UserNotificationDispatcher.cs:143-148`), so the events are dropped as "no subscribers" before reaching the UI. There is also no web handler for them.
2. **Resolved model invisible for Pi.** The OpenCode runtime emits `model.resolved` with field `resolvedModel` (`packages/runner/src/runtime/opencode/event-projection.ts:114,264`) — consistent end to end. The Pi runtime instead emits a `status` event with field `model` (`packages/runner/src/runtime/pi/projector.ts:36`); `"status"` is not in the server's transcript allowlist (`TranscriptAccumulator.cs`), so Pi's model-resolution signal is silently lost. The server is also internally inconsistent: the grain reads `resolvedModel ?? model` (`AgentSessionGrain.cs:1645-1647`) while the transcript summary projector reads only `resolvedModel` (`TranscriptEventSummaryProjector.cs:33`). Finally, the web's `model.resolved` live-event type declares a `model` field (`packages/web/src/entities/agent/model/types.ts:105`), inconsistent with the `resolvedModel` both runtimes emit and the rest of the web reads (it has no active live consumer today, so it is latent).
3. **Follow-up input activity mismatch.** The server's `ShouldRecordActivity` excludes a follow-up `session.input` (`source="followup"` + non-empty `operationId`) and both follow-up terminals from refreshing `LastDataAt` (`AgentSessionGrain.cs:1130-1148`). This exclusion is **intentional and spec-tested**: a rejected/idle follow-up must leave the session `inactive` so Compact/Reset stay available immediately (`AgentSessionRecoveryGrainSpecs.cs:288-326`, asserting `StatusName == "inactive"` at line 322). Meanwhile the web renders every `session.input` as a new "Follow-up" round and flips `isThinking(true)` on the input alone (`useSessionTranscript.ts:372-385`). The session card derives status from that same un-refreshed `LastDataAt`. So the page can show a fresh active round while the session is reported inactive.

Constraints: server active-time semantics and the recovery invariant are load-bearing (Compact/Reset correctness) and MUST NOT regress. No schema migration, no public API change, no new external dependency. No real network/process/wall-clock in tests.

## Goals / Non-Goals

**Goals:**
- Make follow-up completion/failure terminal events reach the web and converge session state.
- Make the resolved model name visible on the web for every runtime (OpenCode and Pi), with one consistent event type and field across all layers.
- Eliminate the page-state vs. session-active-time disagreement for follow-up user input, without weakening the recovery invariant.

**Non-Goals:**
- No restructuring of the follow-up flow, the lease/Compact/Reset machinery, or the runner's local terminal-fallback (durable outbox).
- No change to the OpenCode runner model-resolution path (already consistent); the shared web `model.resolved` event type is aligned, not the OpenCode emission.
- No Pi `status` event generalization (thinking-level / `stopReason` / `variant`); only the model-resolution signal is in scope.
- No server-side dedup, cross-runner coordination, or new persistence.

## Decisions

### D1 — Deliver and handle follow-up terminal events on the web

Add `session.followup_completed` and `session.followup_failed` to the web canonical transcript set (`canonical-event-types.ts` `TRANSCRIPT_EVENT_TYPES`). This both subscribes them (unblocking the server delivery filter) and includes them in the typed transcript routing surface. Handle them as transcript terminals in the session view: close the in-flight follow-up round to the corresponding terminal outcome (completed/failed), and invalidate the `agent-session` / `agent-activity` queries so the session card/list refetches the server-derived status. Reuse the existing `session.closed` rendering/invalidation pattern (`useSessionTranscript.ts:462`, `useWorkflowRunSessions.ts:75`) rather than adding a new dispatch domain.

Note: these are transcript events, so they flow through the transcript path, not the reverse-DNS `ROUTE` table (`handle-event.ts:241`). The compile-time subscription guard (`handle-event.ts:308`) stays satisfied because the new types are added to the canonical set.

**Alternatives considered:** Convert follow-up terminals into reverse-DNS CloudEvents and route via `ROUTE`. Rejected — they are runtime/transcript facts by existing design (`TranscriptAccumulator.cs` maps them to `TranscriptPartTypes.Status`), and reclassifying them would touch the server event catalog and accumulator for no behavioral gain.

### D2 — Unify the resolved-model event; Pi emits `model.resolved` / `resolvedModel`

Make the Pi projector emit a `model.resolved` event carrying `resolvedModel` (normalized to the `<provider>/<model>` form used by OpenCode where the parts are available, otherwise the raw model string), instead of collapsing model changes into the dropped `status` event. Keep the remaining `status` concerns (`thinking_level_changed`, `turn_*`, `agent_end`, `variant`, `stopReason`) unchanged and out of scope. The OpenCode runner path is untouched. The shared web `model.resolved` live-event contract (`packages/web/src/entities/agent/model/types.ts:105`) currently declares a `model` field; align it to `resolvedModel` so the live-event type matches the field both runtimes emit and the rest of the web reads. (There is no active live consumer of this event today, so this removes a latent runner↔web field-name inconsistency rather than fixing an active rendering bug.)

On the server, standardize on a single field: read `resolvedModel` in **both** the grain (`AgentSessionGrain.cs:1645-1647`) and the transcript summary projector (`TranscriptEventSummaryProjector.cs:33`), and remove the now-dead `model` fallback in the grain so live state and summary can no longer disagree. The model-resolution turn-part mapping (`TranscriptEventTypes.cs:31`) is already correct and unchanged.

**Alternatives considered:** (a) Keep the grain's `model` fallback and add the same fallback to the projector. Rejected — it preserves two accepted field names instead of one, leaving the door open to future divergence; only OpenCode and Pi emit this event, and both will use `resolvedModel`. (b) Normalize Pi's `status` event server-side instead of in the runner. Rejected — `"status"` is not an accepted transcript type and bundling model info with thinking-level/stop-reason semantics is the wrong boundary; normalization belongs where the runtime fact is produced (runner), matching the OpenCode precedent.

### D3 — Reconcile follow-up activity state on the web; keep server activity semantics unchanged

The session's active/inactive status remains the server's authority, anchored on the activity window (`AgentSessionJsonHelper.StatusName`, 5-min `LastDataAt` window). Because a rejected follow-up's `session.input` is uploaded before its `session.followup_failed` terminal, refreshing `LastDataAt` on the input would make the session `active` after rejection and break the recovery invariant (`AgentSessionRecoveryGrainSpecs.cs:322`). Therefore the fix is web-side: the follow-up `session.input` is still rendered as a round (the user's prompt is always shown), but the "active/thinking/streaming" indicators that imply an active turn are driven by runtime **response** events (`message.delta`, `reasoning.delta`, `tool_call.*`) — the same events that refresh server activity — not by the `session.input` event alone. The page thus never claims "active" while the server reports "inactive". For a rejected follow-up, the now-delivered `session.followup_failed` (D1) renders the round as failed, consistent with the inactive status.

**Alternatives considered:** (a) Refresh `LastDataAt` on the follow-up input and reset it on `session.followup_failed`. Rejected — it mutates tested recovery logic, introduces a terminal-that-clears-activity semantic, and is squarely against the "no recovery-flow change" non-goal; higher risk than the symptom warrants. (b) Stop rendering the follow-up input as a new round at all. Rejected — it hides the user's submitted prompt and degrades the transcript.

## Risks / Trade-offs

- **[Follow-up terminal handling grows the transcript path]** -> Mitigation: mirror the existing `session.closed` handling; no new dispatch domain; keep the change to subscription + one handler per view.
- **[Pi `model_change` payload shape is uncertain]** -> Mitigation: normalize defensively (accept object or string), fall back to the raw model string; add a projector unit test against the catalog/`splitModel` shape (`runtime/pi/runtime.ts:91`).
- **[Removing the server `model` fallback drops an unanticipated emitter]** -> Mitigation: only OpenCode and Pi emit `model.resolved`; verify via the runtime-event allowlist and tests; if a third source appears, it MUST use `resolvedModel`.
- **[Web shows a brief "pending" state on a follow-up before the first response]** -> Mitigation: the submitted prompt renders immediately; only the active/thinking affordance is gated on response events. Trade-off accepted for honesty vs. the previous false "active" claim.
- **[Recovery invariant regression]** -> Mitigation: server `ShouldRecordActivity` and `StatusName` are untouched; the existing `Compact_AfterFollowupPromptRejected_*` spec remains the guardrail.

## Migration Plan

- D1 and D2 are additive (new subscription types, new normalized emission, field standardization). No schema/API change; no data backfill — new events simply start arriving and old Pi sessions remain without a resolved-model summary until a new model resolution occurs.
- D3 is a web presentation change; no server contract change.
- Deployment order is unconstrained (runner/server/web are independently deployable; each side degrades gracefully if the other is not yet updated — events are ignored until handled, as today).
- Rollback: revert the change commits per package. No persistent state to undo.

## Open Questions

- Exact shape of the Pi `model_change` event's model field (flat `provider/id` string vs. structured object) — confirm at implementation against the Pi SDK session events and pick the normalization accordingly.
- Whether to render a distinct "pending follow-up" affordance for the brief pre-response window, or reuse the existing (response-gated) thinking indicator. Cosmetic; decide during web implementation.
