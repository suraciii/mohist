# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: Server spec fixtures that append registered CloudEvents directly
  Evidence: `EventStore.AppendAsync` now enforces required lineage at `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:49`, but several existing direct-event factories were not migrated. `dotnet test Mohist.sln -p:SkipWebBuild=true --no-restore` fails 24 of 2,775 server specs. For example, `packages/server/tests/Mohist.Server.SpecTests/Specs/Api/ProjectEventsApiSpecs.cs:713-724` still emits `issueno` instead of required `issue`, and `:759` creates agent-session envelopes without `projectid` and `sessionid`. Equivalent invalid workflow/issue fixtures remain in `EventStoreScopedAppendSpecs.cs:305`, `DeadLetterStoreSpecs.cs:166`, and `EventDispatcherImmediateTriggerSpecs.cs:57,137`. [disallowed:not a small local repair; it requires a protocol-aware migration of fixtures across independent API, event-store, dead-letter, and dispatcher suites]
  SuggestedAction: Update every direct fixture for a registered type to provide its catalog-required extensions, or use an intentionally unregistered `test.*` type where the test is not exercising protocol behavior.
  Verification: `dotnet test Mohist.sln -p:SkipWebBuild=true --no-restore`
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs` and issue-event snapshotting
  Evidence: Link commits `EpicIssueRow` plus its durable epic event at `EpicGrain.cs:145-148`, then writes the issue's denormalized `EpicId` only afterward at `:167`. Unlink has the inverse gap at `:421-427`; batch link repeats it at `:330-364`. `IssueStore` stamps `epicid` exclusively from the current cached value at `packages/server/src/Mohist.Server/Infrastructure/Data/Issue/IssueStore.cs:69`. An issue mutation accepted between the membership commit and `SetEpicAffiliationAsync` therefore records no `epicid` after a link, or a stale `epicid` after an unlink. The later durable handler can repair state but cannot repair that immutable historic envelope. `EpicAffiliationLineageSpecs.cs:42-71` only tests the fully sequential path and does not exercise this interleaving. [disallowed:requires an architectural/data-consistency decision across two aggregates]
  SuggestedAction: Make membership visibility and the issue-side affiliation snapshot a single serialized/atomic transition before another issue event can be accepted, then add controlled link and unlink interleaving specs.
  Verification: Pause `SetEpicAffiliationAsync` after the epic transaction commits, emit an issue event, and assert the persisted extensions match the committed link or unlink state in both orders.
  Status: unresolved

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs`
  Evidence: `SaveIssueAsync` sets `_issueReloadRequired` after a failed event-aware save at `IssueGrain.cs:689-705`; normal operations reject that dirty activation through `EnsureIssue` or `RejectIfReloadRequired` at `:710-727`. The new `SetEpicAffiliationAsync` entry point at `:95-112` bypasses both guards and invokes `SaveIssueAsync`. A durable affiliation redelivery arriving before deactivation can persist the failed command's still-pending events and mutated state through an unrelated affiliation update.
  SuggestedAction: Call `RejectIfReloadRequired()` before reading or mutating `_issue` in `SetEpicAffiliationAsync`, and add a regression spec that fails an event-aware save before invoking the affiliation command.
  Verification: Inject a store failure, invoke `SetEpicAffiliationAsync` before reactivation, and assert it throws without a second store write or event append.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionLineage.cs`
  Evidence: The approved matrix limits `issue`/`workflowrunid`/`stage` to workflow-origin sessions in `openspec/changes/issue-412/specs/event-lineage-stamping/spec.md:28-33` and `tasks.json:96-99`. The implementation additionally stamps `issue` for `agent-launch` sessions at `AgentSessionLineage.cs:72-76`, and the new test codifies that divergent behavior at `packages/server/tests/Mohist.Server.UnitTests/Events/AgentSessionLineageTests.cs:54-61`. This gives generic agent launches an issue subscription scope not present in the reviewed specification; the helper's own documentation at `AgentSessionLineage.cs:22-25` also says workflow/issue origin only. [disallowed:product behavior/specification decision]
  SuggestedAction: Either remove the agent-launch projection and assert omission, or amend the product/design specs and persistence specs to explicitly define agent-launch issue context as lineage.
  Verification: Exercise agent-launch sessions with issue context through `AgentSessionStore.SaveAsync` and assert the selected contract on persisted envelopes.
  Status: unresolved

- [ID: item-5]
  Severity: warning
  Scope: `EventCatalog` completeness and producer conformance
  Evidence: The runtime gate treats an unknown type as conforming: `EventCatalog.RequiredAttributes` returns an empty list at `packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs:251-262`, and `EnvelopeConformance` consequently no-ops at `EnvelopeConformance.cs:22-26`. `EventStore` accepts that envelope at `EventStore.cs:47-49`. `ProducedTypes` is checked only by `EventCatalogTests.cs:29-44`, not by production code. A future direct CloudEvent producer can therefore introduce an unregistered `com.mohist.*` event without required lineage and still persist it, contrary to the requirement that adding an event category without stamping must fail validation.
  SuggestedAction: Reject unknown Mohist protocol types at the event-store boundary, or add a complete producer-registration mechanism and an executable completeness check that covers direct producers as well as serializers.
  Verification: Add a direct producer of an unregistered `com.mohist.*` type and assert the build-time or append-time conformance mechanism fails.
  Status: open

- [ID: item-6]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/Epic/EpicRecoverySpecs.cs`
  Evidence: T-004 requires durable self-healing for both link and unlink. The failure/redelivery scenario at `EpicRecoverySpecs.cs:142-170` exercises only `EpicIssueLinked`; the unlink event in the out-of-causal-order scenario at `:105-137` has `failFirst: false`. A failed unlink affiliation write could retain a stale `EpicId` until a successful replay, but that recovery path is not verified.
  SuggestedAction: Add a failed `EpicIssueUnlinked` delivery followed by redelivery. Assert the cached affiliation remains until retry, clears after retry, and its source event is marked dispatched only after success.
  Verification: Run the `EpicRecoverySpecs` collection and the full server suite.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Issue/Domain/BackfillIssueEpicAffiliationMigrationSpecs.cs`
  Evidence: The migration spec verifies initial population at `BackfillIssueEpicAffiliationMigrationSpecs.cs:23-76`, but does not re-run the migration, preserve an already live-written `Issue.EpicId`, or seed an existing event row to prove that the backfill never changes historical `ExtensionsJson`. These are material parts of the idempotency and production-time snapshot contract.
  SuggestedAction: Add cases for a pre-populated different `epicId`, a second migration execution, and an existing issue-event envelope whose extensions must remain byte-for-byte unchanged.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter FullyQualifiedName~BackfillIssueEpicAffiliationMigrationSpecs`.
  Status: open

- [ID: item-8]
  Severity: minor
  Scope: lineage conformance validation and changed inbox tests
  Evidence: Producers consistently treat whitespace as absent (`WorkflowRunLineage.cs:47-53`, `AgentSessionLineage.cs:92-100`), but the global conformance guard only rejects `null` or `""` at `EnvelopeConformance.cs:29-35`. Thus a direct producer can persist whitespace for a required lineage value. The tests only cover an empty string at `EventCatalogTests.cs:264-274`. Additionally, new/modified inbox fixture paths use wall-clock time at `InboxProjectionTestSupport.cs:51,65` and `InboxProjectionHandlerSpecs.cs:512`, contrary to `design/testing.md:53-59`.
  SuggestedAction: Make the conformance guard use `string.IsNullOrWhiteSpace`, add whitespace cases, and use a fixed timestamp in the changed fixture scenarios.
  Verification: Run `EventCatalogTests` and `InboxProjectionHandlerSpecs` with fixed-time assertions.
  Status: open

## Follow-up Items

- [ID: item-9]
  Severity: follow-up
  Scope: `design/event-protocol.md`
  Evidence: The implementation-gap section still says the current code does not stamp `issue`, `epicid`, or `workflowrunid` and that `EventCatalog` is a pure constant table (`design/event-protocol.md:142-149`), while this candidate implements all of those. The document's only issue-412 update is the feedback-stage matrix row at `:69-70`.
  SuggestedAction: When the implementation blockers are resolved, update the gap note to describe the remaining difference or remove the now-resolved statements, following the repository's spec-versus-current-state convention.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-10]
  Severity: warning
  Scope: existing non-transactional epic event persistence
  Evidence: Several epic transitions still commit state before the post-commit, exception-swallowing append path. `EpicGrain.CreateAsync` saves at `EpicGrain.cs:63-66`; the helper documents the intentional loss window at `:1105-1145`. A crash or append failure can leave committed epic state with no durable event, including no lineage-stamped envelope. This behavior predates the lineage work and is explicitly retained by the candidate, but it weakens the event/recovery guarantees that this feature relies on.
  SuggestedAction: Track a separate atomic-outbox/transactional epic-event persistence change and cover create, update, pause, terminal, and auto-done paths.
  Status: pre-existing

<promise>FAIL</promise>
