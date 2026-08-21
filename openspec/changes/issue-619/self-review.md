# Self-Review: issue-619

## Review mode

Re-review. I reread the current issue body and its planning-reset comment before checking the updated artifacts. I verified the three findings from the previous review against the current proposal, design, specs, and task breakdown.

## Verdict

PASS — no must-fix problems remain; the plan is ready to build.

## Previous findings verification

- **MF-1 — canonical diagnostics:** Fixed. `design.md` Decisions 1 and 4 now require the authorized diagnostic route to call `AgentReadinessService`, pass through the lossless `AgentExecutabilityResult`, and expose the canonical state, concrete gaps, gap next actions, and Connection diagnostics. T-002 and the admission spec require structural and history-based diagnostic coverage while keeping those details out of the caller nudge.
- **MF-2 — explicit `new task` classification:** Fixed. `proposal.md`, `design.md` Decision 1, the admission spec, and T-002 explicitly classify the leading marker as new work before DM mapping and the backpressure short-circuit. They separately preserve ordinary established-session DM follow-ups.
- **MF-3 — end-to-end boundary:** Fixed. `design.md` Decision 7, the ownership spec, and T-004 now require a separate harness using the actual Server ingress HTTP route and actual Node adapter event handler. The acceptance criteria cover both Server-owned durable delivery with no direct post and adapter-owned fallback with one direct post; shared fixtures are explicitly treated as wire-level coverage only.

No regression was introduced by these fixes. The added requirements remain consistent with the issue's no-duplicate owner boundary and no-execution side-effect requirements.

## Review dimensions

### Issue basis

Checked, no issue. The plan addresses all seven issue acceptance criteria: admission gating and safe guidance, authorized diagnostics, explicit response ownership, preservation of the direct fallback, deduplication through uncertainty and reconciliation, combined ingress/adapter testing, and preservation of existing follow-up, Disabled, executable, and unknown-readiness behavior.

### Coverage

Checked, no issue. The artifacts cover ordinary new DMs, explicit `new task` DMs with an existing mapping, channel roots, unbound-thread first mentions, established follow-ups, Disabled Connections, executable and unknown readiness, unavailable Connections, safe summaries, diagnostic projections, durable identity, concurrency/redelivery, uncertainty, and both response owners.

### Correctness

Checked, no issue. The proposed ordering classifies routing before provider-inbox or launch state, gates only new work, uses the canonical readiness result, persists a bounded Connection-plus-Slack-event nudge identity, and makes the adapter defer to Server-owned delivery. The direct fallback is limited to the no-intent path, and unexpected persistence failures remain unacknowledged rather than being falsely converted into a direct response.

### Consistency with the current codebase and conventions

Checked, no issue. The plan reuses the existing `AgentReadinessService`, `ConnectionDiagnostic`, `SlackOutboxStore.EnqueueRequiredAsync`, `UserAction` outbox kind, provider `client_msg_id` reconciliation, Slack inbox routing, and existing Node/Go transport boundaries. It explicitly keeps `AgentReadinessDeriver` only for compatibility facts and does not change readiness rules or introduce another delivery store.

### Task breakdown, ordering, and verifiability

Checked, no issue. T-001 establishes the wire and adapter contract before T-002/T-003 integrate the shared admission behavior; T-004 then verifies durable delivery and the cross-component boundary. Each task has concrete acceptance criteria for side effects, ownership, identity, safe text, diagnostics, acknowledgment behavior, and tests. The prior missing diagnostic, marker-classification, and E2E requirements are now assigned and verifiable.

## Observations

- The exact mapping of every enabled Connection state beyond explicit backpressure to “unavailable” remains an open question in `design.md`. The plan names the relevant setup, credential, and service-offline states and preserves existing owner/access and identity-drift policies, so this is not a must-fix plan defect; the implementation should settle the boundary before coding.
- The preferred channel-root nudge anchor (a root-thread reply versus another root-context presentation) remains open. Either proposed anchor preserves the same conversation and originating message context required by the issue.
- Exact caller wording and whether to include a safely authorized diagnostic link remain product choices. The plan already requires fixed, actionable, generic summaries and forbidden-content tests, which is sufficient for the issue.
- The migration retains tolerant decoding for older Server responses. This is an explicit compatibility decision and does not weaken the new Server-owned durable-nudge path.

<promise>PASS</promise>