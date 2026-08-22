# Review: issue-639

## Verdict

FAIL — one previous must-fix finding remains unresolved: the changed Server boundary still leaves the full Server SpecTests assembly failing.

## Re-review disposition

- **Previous MF-1 — valid empty Workflow input responses were rejected by `ServerConnection`: fixed.** `packages/runner/src/server/connection.ts:524` now preserves a valid empty JSON array when the Server returns `[]` for a submitted batch, while still rejecting non-empty count mismatches. The connection test, delivery-adapter test, and real-adapter outbox test cover the path through to already-consumed settlement. The focused Runner suites passed 88 tests and both Runner typechecks passed.
- **Previous MF-2 — the Server SpecTests assembly was red after the shared boundary change: not fixed.** The recorded progress explicitly leaves this unresolved, and a fresh full run still fails 48 of 3005 tests. No valid won't-fix justification is present; the failures are direct effects of the changed runtime-event boundary.

## Must-fix findings

### MF-1 — The changed Workflow-session boundary is not integrated with the existing Server callers and fixtures

**Where:** `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1698-1711`, especially the persisted `SourceKind == "workflow"` gate.

The gate correctly enforces the issue's fail-closed rule for a Workflow-introduced session: a current-binding unattributed batch is accepted only when it is a non-empty pure `session.activity` batch. However, the surrounding Server integration/spec callers still create Workflow-labelled sessions and submit unattributed non-activity runtime events through the session route or directly to the grain. Those callers now receive `InvalidOperationException: Workflow runtime events require the acknowledged Agent turn binding.` and the full Server SpecTests assembly remains red:

```text
Failed: 48, Passed: 2957, Total: 3005
```

Representative failures include:

- `AgentSessionContextEventPublishingSpecs.SessionClosed_FailedWithContextExhaustion_PersistsContextExhaustedEventRow`
- `AgentSessionLifecycleDedupSpecs.AttachThenFirstRuntimeAppend_PersistsRuntimeBoundOnceAcrossRuntimeEvents`
- `AgentSessionGrainPersistSuccessSpecs.Persistence_SavesBoundRuntimeEventsAndTranscript`
- `AgentSessionReconnectRecoveryGrainSpecs.ReconcileMissingBinding_UnknownSession_SettlesIdleAndRebindsAtomically`
- `WorkflowSessionSpecs.GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder`
- `GenericAgentSessionCanonicalFollowupApiSpecs.IssueSessionMetadata_ExposesFollowupInputAndTurnStatus`

This violates T-001's Server integration/spec-test acceptance criterion in `openspec/changes/issue-639/tasks.json`: the changed boundary must be covered and the focused Server verification must pass without leaving surrounding Server callers incompatible. It also fails the review requirement that the changed behavior be covered and verified consistently with the surrounding codebase. The focused new boundary classes pass, but that does not make the shared contract integrated when 48 existing callers fail at the changed guard.

Align the affected route/fixture/direct-caller boundaries with the new contract and rerun the full Server SpecTests assembly. Do not weaken the product rule: Workflow-labelled sessions must continue rejecting unattributed non-activity and mixed batches; legitimate generic/session-follow-up callers should use the appropriate generic fixture or provide the complete acknowledged identity.

## Review dimensions

- **Issue basis: checked, no new issue.** The issue acceptance criteria and the capability specs were reread before evaluating the re-review. The required activity-only relaxation, fail-closed attribution, outbox convergence, retry preservation, retention warning behavior, and live-delivery liveness remain the review basis.
- **Previous findings: FAIL.** The empty-array delivery finding is properly fixed. The Server integration/test failure finding remains open and is independently sufficient for FAIL.
- **Regression check: checked, no additional must-fix issue.** The `ServerConnection` change now permits the required valid empty response without accepting a non-empty count mismatch. The pure current-binding active/idle activity path and no-turn non-activity/mixed rejection path pass their focused Server tests. The focused Runner convergence and adapter suites passed.
- **Correctness and consistency: FAIL.** The persisted `SourceKind` boundary matches the plan/spec, but the change is not complete in the surrounding Server codebase while the full assembly still fails at that boundary.
- **Tests: FAIL.** Runner typechecks and focused Runner tests pass; the focused Server boundary classes pass, but the full Server SpecTests assembly still reports 48 failures caused by the changed runtime-event boundary.

## Observations

- The previous empty-response defect is addressed at the actual connection boundary, not only in a mock: `connection.test.ts`, `runtime-event-delivery.spec.ts`, and `runtime-event-outbox.spec.ts` exercise the valid `[]` response and the two-empty already-consumed settlement.
- The current grain implementation correctly uses persisted `SourceKind == "workflow"` rather than payload source or the presence of an existing Workflow turn. The focused tests cover active and idle activity-only acceptance, no-turn non-activity/mixed rejection, stale binding no-op behavior, and absence of Workflow observations.
- The deterministic refusal allowlist, three-refusal settlement, cleanup empty-receipt path, persistence ordering, retention warning edge tracking, and saturated-group Runner coverage showed no additional must-fix problem in the executed focused suites.

<promise>FAIL</promise>
