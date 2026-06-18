# Review Report

## Result: PASS

The post-repair candidate satisfies issue 126's scoped engine requirements. `WorkDispatch` carries an additive owner-kind dimension with fresh serializer slots, `RunnerGrain` branches the three coordination weld points while preserving workflow report response shape, `AgentJobGrain` owns the standalone pending/running/terminal lifecycle and dispatches directly through `RunnerRegistry`, the validation API exercises HTTP -> grain -> runner -> report -> response, and the runner now preserves agent-job owner identity through poll, report, in-flight tracking, and artifact uploads. The focused runner and server suites passed on this snapshot.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: prior-review artifact upload path
  Evidence: The earlier review found agent-job artifact uploads were routed through `/api/workflow-runs//...`. The current snapshot now routes executor uploads by owner kind in `packages/runner/src/runtime/executor.ts:187`, passes the kind through `packages/runner/src/runtime/artifact-capture.ts`, builds `/api/agent-jobs/{agentJobId}/work/{workId}/artifact-uploads` in `packages/runner/src/server/connection.ts:125`, and serves that route in `packages/server/src/Mohist.Server/Api/WorkflowArtifactUploadRoutes.cs:60`.
  Verification: `npm test -- --run tests/executor-artifacts.spec.ts tests/server-connection-artifacts.spec.ts` passed 22/22; `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~WorkflowArtifactUploadRouteSpecs|FullyQualifiedName~AgentJobOwnerKindSpecs|FullyQualifiedName~AgentJobGrainSpecs|FullyQualifiedName~AgentJobRoutesSpecs"` passed 38/38.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: prior-review runner report owner-kind normalization
  Evidence: The earlier review called out case-sensitive owner-kind handling. The current snapshot normalizes runner report payloads in `packages/runner/src/server/connection.ts:28` and server `/report` handling in `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:87`, with tests covering uppercase runner owner-kind at `packages/runner/tests/server-connection-artifacts.spec.ts:197`.
  Verification: Focused runner suite passed 22/22; focused server suite passed 38/38.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: prior-review duplicate submit and retry visibility
  Evidence: The current snapshot preserves `SubmitAsync` single-shot behavior in `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:172`, exposes `DispatchAttempts` in `packages/server/src/Mohist.Server/Agent/Grains/IAgentJobGrain.cs:23`, and tests second submit rejection plus retry-attempt increments in `packages/server/tests/Mohist.Server.Tests/Specs/Agent/Grain/AgentJobGrainSpecs.cs:306` and `packages/server/tests/Mohist.Server.Tests/Specs/Agent/Grain/AgentJobGrainSpecs.cs:322`.
  Verification: Focused server suite passed 38/38.
  Status: resolved

- [ID: item-4]
  Severity: info
  Scope: prior-review workflow regression and wire compatibility coverage
  Evidence: The current snapshot adds workflow negative assertions in `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain/AgentJobOwnerKindSpecs.cs:274` and pins old JSON-style payload defaults in `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain/AgentJobOwnerKindSpecs.cs:414`.
  Verification: Focused server suite passed 38/38.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Api/WorkflowArtifactUploadRoutes.cs:184`
  Evidence: The agent-job upload endpoint reuses workflow artifact upload storage/table names and response field `workflowRunId` to carry an agent-job id. This is acceptable for issue 126 because the endpoint is validation-path plumbing for runner artifacts, but the naming will be confusing once standalone agent jobs become a product surface.
  SuggestedAction: When the visibility/read-model issue adds first-class agent-job artifacts, introduce agent-job-specific response names or a neutral owner field.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:237`
  Evidence: `TryAssignToRunnerAsync` transitions the grain to `Running` before `runner.AssignWorkAsync`; if assignment throws or rejects, the job fails with `runner-unavailable` rather than retrying another runner. That is now covered as intentional fail-fast behavior for a selected runner failure, while no-slot cases still back off and retry.
  SuggestedAction: Revisit this policy if validation traffic expands beyond smoke testing and transient per-runner assignment failures should not terminally fail the job.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/runner/src/server/connection.ts:72`
  Evidence: `uploadArtifact` branches on exact lower-case `ownerKind === "agent-job"`. Real agent-job work is server-generated with the lower-case `WorkDispatchOwnerKinds.AgentJob` constant and poll preserves that value, so this is not a current correctness bug. It is slightly less defensive than the normalized report path.
  SuggestedAction: Normalize the optional `ownerKind` parameter in `uploadArtifact` if future non-server callers are introduced.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-8]
  Severity: warning
  Scope: `dotnet test` build output / client package audit
  Evidence: The focused server test command runs the web build and reports `npm audit` output with 8 vulnerabilities (3 moderate, 2 high, 3 critical). This is package-audit debt surfaced by the build step and not introduced or remediated by the issue 126 engine changes.
  SuggestedAction: Track dependency audit remediation separately so it does not block this scoped engine change.
  Status: pre-existing

- [ID: item-9]
  Severity: info
  Scope: .NET SDK / Vite warnings
  Evidence: The focused server test command prints NETSDK1057 because the environment uses a .NET 11 preview SDK, and Vite prints Rollup pure-annotation warnings from `@microsoft/signalr`. Both are environmental/dependency warnings; tests completed successfully.
  SuggestedAction: No action for issue 126.
  Status: pre-existing

<promise>PASS</promise>
