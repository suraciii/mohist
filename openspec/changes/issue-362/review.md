# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Events/Grains/DispatcherGrain.cs`
  Evidence: `IDispatcherGrain` accepts any string key (`IDispatcherGrain.cs:17`), while `DispatcherGrain.OnActivateAsync` registers the dispatch reminder for whichever key was requested (`DispatcherGrain.cs:37-45`). It never enforces `FixedKey`. A server caller can activate `GetGrain<IDispatcherGrain>("other")`, creating a second reminder/notifier that concurrently pulls and delivers the same rows. This violates the cluster-singleton and serial FIFO guarantees; the service itself requires external serialization (`EventDispatcherService.cs:75-80`).
  SuggestedAction: Reject every grain key except `DispatcherGrain.FixedKey` before registering a reminder or dispatching, and keep all callers on the fixed key.
  Verification: Resolve a non-fixed key and assert that it cannot register a reminder or invoke a handler; assert the fixed key remains the only dispatcher activation.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs`, `packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs`
  Evidence: An admitted poll holds `_pollAdmissionGate` but releases `_lifecycleGate` after `TouchPresenceAsync` (`DispatchService.cs:64-85`). `UnregisterAsync` only takes `_lifecycleGate` (`RunnerGrain.cs:156-173`), clears the persisted profile and closes existing work, then the already-admitted poll continues with its previously read `RunnerInfo` and can call `AssignWorkerAsync` for a new workflow (`DispatchService.cs:145-155`). The new work is returned to an unregistered runner and cannot be recovered through its cleared profile.
  SuggestedAction: Serialize unregister with an admitted poll, or revalidate runner registration immediately before every workflow claim under the same admission boundary.
  Verification: Pause an admitted poll after it reads `RunnerInfo`, unregister the runner, then resume the poll. It must return no newly claimed workflow work.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Event.cs`, `packages/cli/Mohist.Cli/Program.cs`
  Evidence: Dead-letter commands resolve the local operator credential before every request (`MohistCliCommands.Event.cs:46,83`) and `MohistCliApi` blindly forwards it as a header (`MohistCliApi.cs:1139-1147`). `MOHIST_SERVER_URL` controls the CLI base address without a loopback restriction (`Program.cs:10-15`), so `mo event dead-letter ...` sends the bearer token to an arbitrary remote endpoint. This contradicts the loopback-only operator boundary.
  SuggestedAction: Require a loopback `HttpClient.BaseAddress` before reading or sending the credential for dead-letter commands.
  Verification: Configure a non-loopback base URL and assert the command fails without reading the token file or sending a request/header.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/OperatorDiagnostic.cs`
  Evidence: `PathPattern` only recognizes POSIX paths and drive-letter Windows paths (`OperatorDiagnostic.cs:52`). A message containing `\\fileserver\share\secret.txt` is returned unchanged through the dead-letter API, despite the path-redaction contract (`DeadLetterRoutes.cs:50`).
  SuggestedAction: Redact UNC paths and add unit plus API coverage for them.
  Verification: Store a dead letter with a UNC path in its error and assert the API response exposes neither the host nor the share/file path.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Security/OperatorCredential.cs`
  Evidence: Explicit `MOHIST_OPERATOR_TOKEN_PATH` and `Mohist:OperatorTokenPath` values flow into `ReadAndSecure`, which rejects every symlink (`OperatorCredential.cs:54-63,105-112`). This makes the documented managed-deployment override unusable with standard secret-volume layouts, where the projected credential file is a symlink. The default generated credential can remain non-symlink protected without imposing that constraint on an explicit operator path.
  SuggestedAction: Apply the non-symlink requirement to the generated default path, and define safe handling for explicitly configured secret paths.
  Verification: Configure an explicit symlinked secret file with a valid token and verify authenticated list/re-delivery succeeds; default generated-file symlinks must still be rejected.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs`, Agent dispatch recovery
  Evidence: After the candidate runner is durably stored, retries only target that runner (`AgentJobGrain.cs:376-380`). When its runtime state is offline, `TryAssignToRunnerAsync` returns false without clearing `RunnerId` (`403-408`), so the healthy runners enumerated at `383-400` are never tried. The job remains pinned until `JobTimeout`, then fails as `report-timeout`, even if the crash happened before the candidate accepted work and another runner is available.
  SuggestedAction: Persist an explicit acceptance checkpoint and distinguish an unaccepted candidate from an acknowledged assignment. Safely release only unaccepted/offline candidates for a new eligible-runner selection.
  Verification: Fail immediately after `AssignmentPreparedAsync`, remove the selected runner, register another eligible runner, reactivate the job, and assert one stable work is assigned to the replacement runner.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: dispatcher fresh-host startup coverage
  Evidence: `DispatcherStartupSpecs` only inspects the reminder table (`DispatcherStartupSpecs.cs:23-34`). The reminder-delivery fixture manually invokes `EnsureStartedAsync` (`DispatcherFixture.cs:374-395`), so no test proves that `DispatcherActivationService` starts a fresh host, an appended event is delivered by its first reminder tick, and no caller used `PulseAsync`.
  SuggestedAction: Add a full-host integration spec that appends a matching event after startup and advances fake time until the hosted-service-registered reminder delivers it.
  Verification: The assertion must observe the handler invocation and row mark without direct dispatcher activation, `ReceiveReminder` invocation, or `PulseAsync`.
  Status: unresolved

- [ID: item-8]
  Severity: test-gap
  Scope: dead-letter redelivery failure persistence
  Evidence: Production failure recovery sets the row back to `Pending` and replaces diagnostics (`DeadLetterStore.cs:127-144`), but the real-store spec only covers `Redelivering -> Resolved` (`DeadLetterStoreSpecs.cs:217-233`). Dispatcher tests use fakes for this branch. The required operator recovery state transition therefore lacks production persistence coverage.
  SuggestedAction: Add a real SQLite store spec for `StartRedeliveryAsync -> RecordRedeliveryFailureAsync`, including unresolved query visibility and updated error/attempt/timestamp values.
  Verification: Persist a row, start recovery, record an exhausted failure, reload it on a fresh context, and assert `Pending` plus the new diagnostics.
  Status: unresolved

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs`, presence expiry after silo loss
  Evidence: The current grain declares `PresenceReminderName` but neither the candidate nor `origin/master` registers it; `ReceiveReminder` is a no-op (`RunnerGrain.cs:64,126-132`). A grain timer is disposed on deactivation (`119-123`), so a runner that never polls after a silo loss is never timed out or closed out. This behavior predates the candidate, but it remains an adjacent recovery risk.
  SuggestedAction: Address persistent runner-presence supervision in its owning scheduling work.
  Status: pre-existing

- [ID: item-10]
  Severity: info
  Scope: local repository refs
  Evidence: Local `master` is 117 commits behind `origin/master`. The reviewed candidate is `origin/master...HEAD` (19 issue commits, 118 files); using local `master...HEAD` incorrectly includes 828 files of upstream work.
  SuggestedAction: Fast-forward the local `master` ref before any base-diff tooling that does not use `origin/master`.
  Status: pre-existing

## Acceptance Criteria Evidence

- The four-table undelivered pull, origin-aware marking, serial retry/settlement, and dead-letter schema are implemented in `EventStore.cs:180-268`, `EventDispatcherService.cs:82-219`, and `DeadLetterStore.cs:25-73`.
- Deliver-before-mark re-delivery with idempotent consumption is covered in `EventDispatcherSpecs.cs:275-311`.
- The fixed-key reminder design is not satisfied because item-1 allows arbitrary keyed dispatcher activations; fresh-host delivery is not verified by item-7.
- Dead-letter query/re-delivery surfaces exist, but items 3-5 leave the operator security and deployment contract incomplete.

## Verification

- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter "FullyQualifiedName~DispatcherGrainSpecs|FullyQualifiedName~DispatcherStartupSpecs|FullyQualifiedName~DeadLetterStoreSpecs|FullyQualifiedName~DeadLetterRoutesSpecs|FullyQualifiedName~DeadLettersMigrationSpecs|FullyQualifiedName~EventDeliveryIndexSpecs|FullyQualifiedName~EventStoreScopedAppendSpecs|FullyQualifiedName~RunnerDefinitionStateSpecs|FullyQualifiedName~DispatchServiceReconciliationSpecs|FullyQualifiedName~AgentJobGrainPersistenceSpecs|FullyQualifiedName~AgentJobOwnerKindSpecs|FullyQualifiedName~AgentLauncherSpecs|FullyQualifiedName~InboxProjectionHandlerRealtimeHintSpecs"` passed: 108 tests.
- `dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj --no-restore --filter "FullyQualifiedName~EventDispatcherSpecs|FullyQualifiedName~OperatorDiagnosticTests|FullyQualifiedName~OperatorCredentialTests|FullyQualifiedName~MohistServiceGraphRegistrationTests"` passed: 29 tests.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore --filter "FullyQualifiedName~CliEventDeadLetterCommandSpecs"` passed: 9 tests.
- `git diff --check origin/master...HEAD` and the final worktree `git diff --check` passed.

<promise>FAIL</promise>
