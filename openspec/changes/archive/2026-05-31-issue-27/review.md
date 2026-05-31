# Review Report

## Result: PASS

## Verified Items

- `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs` now restores persisted lease state during activation and through `RestoreLeaseAsync()`, and `GetWorkAsync()` reconciles that restored lease before any new dispatch.
- `GetAssignedRunnerIdAsync()` and `GetAssignedWorkIdAsync()` now reload persisted lease state after grain reactivation, so owner/status reads stay aligned with durable lease ownership.
- `ReconcileLeaseAsync()` keeps workflows recovery-blocked when a persisted lease owner is still registered but unavailable, and only routes redispatch through `AbandonCurrentWorkAsync()` when the prior owner is provably offline.
- `packages/server/src/Mohist.Server/Workflow/Recovery/WorkflowBacklogRecoveryService.cs` resolves project identity from persisted workflow variables when indexed metadata and annotations are missing, and it skips leased workflows instead of creating a duplicate dispatch opportunity.
- `packages/server/tests/Mohist.Server.Tests/Specs/WorkflowLeaseActivationSpecs.cs` covers activation-time lease preservation and incomplete lease blocking.
- `packages/server/tests/Mohist.Server.Tests/Specs/RunnerFailureSpecs.cs` covers offline-owner recovery, intervening abandonment before redispatch, and registered-but-unavailable owners remaining blocked.
- `packages/server/tests/Mohist.Server.Tests/Specs/WorkflowBacklogRecoverySpecs.cs` covers variable-based project recovery, leased-workflow skip behavior, and explicit non-default handling when project identity is missing.
- `packages/server/tests/Mohist.Server.Tests/Specs/AgentSessionSpecs.cs` continues to verify that activity/session reads do not report a stale active owner when lease ownership differs.

## Verification

- `dotnet test packages/server/tests/Mohist.Server.Tests -p:SkipWebBuild=true --filter "FullyQualifiedName~WorkflowLeaseActivationSpecs"`
- `dotnet test packages/server/tests/Mohist.Server.Tests -p:SkipWebBuild=true --filter "FullyQualifiedName~RunnerFailureSpecs"`
- `dotnet test packages/server/tests/Mohist.Server.Tests -p:SkipWebBuild=true --filter "FullyQualifiedName~WorkflowBacklogRecoverySpecs"`
- `dotnet test packages/server/tests/Mohist.Server.Tests -p:SkipWebBuild=true --filter "FullyQualifiedName~AgentSessionSpecs"`

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Recovery/WorkflowBacklogRecoveryService.cs`
  Evidence: The spec language for missing project identity and blocked recovery mentions an explicit diagnostic. The current implementation logs warnings and avoids incorrect backlog claims, which is safe for issue #27 correctness, but it does not yet persist a durable recovery diagnostic record.
  SuggestedAction: Consider adding a durable operator-visible recovery diagnostic for missing project identity or unreconciled blocked lease state in a later change.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/AgentSessionSpecs.cs`
  Evidence: `RunnerReport_WhenAgentWorkFailsBeforeTelemetry_ClosesCreatedSession` remains skipped by design decision and is unrelated to the lease-recovery correctness fixed in issue #27.
  SuggestedAction: Leave as-is unless separate session-closing work intentionally takes it on.
  Status: pre-existing

<promise>PASS</promise>
