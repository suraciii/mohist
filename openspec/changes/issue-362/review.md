# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs`, Agent-job poll recovery
  Evidence: `DequeueAssignedAgentJobAsync` persists a pending work item as `Running` before returning it to the runner at lines 270-303. `DispatchService.PollAsync` neither includes agent jobs in its reported-work reconciliation nor reoffers running agent jobs at lines 51-67. A lost poll response or runner-process crash after this state write therefore leaves the work permanently unoffered until `AgentJobGrain` times out and fails it. This contradicts the required recovery after Runner acceptance. [disallowed:product-behavior]
  SuggestedAction: Give agent-job polling an acknowledgement/reconciliation state equivalent to workflow work, and reoffer the stable work id until the runner reports it in-flight or reports its result.
  Verification: Poll an assigned agent job, discard the response, then poll again with empty `inFlight` and `awaitingAck`; the same work must be returned rather than timing out.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs`, mixed workflow and Agent capacity
  Evidence: After appending an agent-job dispatch at lines 51-53, `PollAsync` computes `spare` from workflow `activeWorkKeys` only at lines 55-65. A one-slot runner with an assigned agent job and a ready workflow can receive both from one poll. The runner executes every returned dispatch concurrently in `packages/runner/src/runtime/host.ts:311-326`, exceeding the capacity atomically enforced only at Agent-job admission.
  SuggestedAction: Account for the dequeued agent-job work when calculating remaining poll capacity, and add a mixed Agent/workflow one-slot regression test.
  Verification: With one configured slot, an assigned runnable Agent job, and a pending workflow task, one poll must contain exactly one dispatch.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs`, concurrent Agent-job dequeue
  Evidence: `RunnerGrain` is `[Reentrant]` at line 33, but `DequeueAssignedAgentJobAsync` reads a pending item at lines 270-282, awaits `IsWorkRunnableAsync` at line 283, then sets that captured item to `Running` at lines 299-303 without a gate. Two overlapping polls can both observe and return the same pending dispatch, duplicating execution.
  SuggestedAction: Serialize dequeue-and-transition, or claim the item durably before an await and revalidate the claim afterwards.
  Verification: Block `IsWorkRunnableAsync`, issue two concurrent dequeues, release the block, and assert that only one receives the dispatch.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs`, retry after downstream publish failure
  Evidence: The handler inserts the inbox projection at line 134 and returns immediately for an existing row at lines 135-138. Its required `InboxItemPersisted` append happens only at lines 161-164. If that append fails after the insert, dispatcher retry sees `AlreadyExisted`, returns successfully, and settles the source event without ever appending the missed durable hint.
  SuggestedAction: Persist or derive a retryable hint-publication state so a duplicate projection delivery can complete a previously failed publish without emitting duplicate hints.
  Verification: Make the first `PublishAsync` fail after a successful insert, replay the source event with publishing restored, and assert exactly one hint event is appended.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/OperatorDiagnostic.cs`, operator diagnostic redaction
  Evidence: `Summarize` only truncates at newline and checks whitespace-delimited tokens that begin with a path at lines 19-46. A single-line error such as `failure at Namespace.Handler() in /tmp/x.cs:line 42` exposes the stack frame, while `path=/srv/private/db.sqlite` exposes the path. `DeadLetterRoutes` returns the summary at lines 50 and 79, violating the no-stack-frames/no-file-paths operator contract. Existing tests cover only newline-separated frames.
  SuggestedAction: Use conservative structured redaction for untrusted messages, including embedded path and stack-frame forms, and test list plus redelivery responses.
  Verification: Cause a handler to throw each example above; neither API response may contain method-frame or filesystem-path content.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Security/OperatorCredential.cs`, `packages/cli/Mohist.Cli/OperatorCredentialProvider.cs`
  Evidence: The server accepts `Mohist:OperatorToken` and `Mohist:OperatorTokenPath` configuration at lines 20-24 and 52-55, but the CLI reads only `MOHIST_OPERATOR_TOKEN` and `MOHIST_OPERATOR_TOKEN_PATH` at lines 23-25 and 37-43. Configuring the server credential through its documented configuration path leaves `mo event dead-letter` unable to obtain the same credential without an undocumented second override.
  SuggestedAction: Define one interoperable override contract for server and CLI credential resolution, and document it with an interoperability test.
  Verification: Configure only `Mohist:OperatorTokenPath`, start the server, then list and redeliver through `mo` without a separate CLI token override.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: dispatcher fake stores
  Evidence: Unit `FakeDeadLetterStore.SettleAsync` marks the source before adding dead letters at lines 45-75, and spec `CapturingDeadLetterStore` has the same ordering at `DispatcherFixture.cs:168-182`. Unlike the production transaction, neither fake can model a failure after the source mark; the dispatcher unit/spec tests therefore cannot detect a partial poison-settlement regression.
  SuggestedAction: Make the fakes atomic or introduce failure injection that verifies neither source mark nor dead-letter row survives either intermediate failure.
  Verification: Run the dispatcher unit and grain specs with a simulated post-mark dead-letter write failure and assert both stores remain unchanged.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs`, four-table pull query
  Evidence: The production query unions all four truth tables at lines 243-260, but `EventStoreScopedAppendSpecs` verifies only the AgentSession branch at lines 191-204. FIFO unit coverage uses a sorting fake. An omission or ordering defect in the WorkflowRun, Issue, or Epic branches would pass current tests despite being a core acceptance criterion.
  SuggestedAction: Add an SQLite spec that inserts interleaved undelivered rows in all four tables and asserts one ordered `(Source, Id)` result before and after marking one origin.
  Verification: Mutate any UNION branch or its ordering in a local experiment; the new test must fail.
  Status: open

- [ID: item-9]
  Severity: minor
  Scope: `docs/cli-reference.md`
  Evidence: The candidate adds `mo event dead-letter list|redeliver`, but the CLI product specification's root-command list at lines 43-62 omits `event` and claims no implementation gaps at lines 329-331. The new operator recovery contract is consequently undiscoverable from the documented command surface.
  SuggestedAction: Add the event/dead-letter commands, credential prerequisite, filter, and recovery-state output to the CLI product specification.
  Verification: Confirm `docs/cli-reference.md` and `mo --help` both describe `mo event dead-letter list|redeliver` consistently.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-10]
  Severity: info
  Scope: server test suite
  Evidence: Full `npm test` passed with 3 retained architecture-test skips and 9 server-spec skips. The workflow artifacts identify these as pre-existing and they are not caused by this candidate.
  SuggestedAction: Remove or replace skipped coverage in the owning issues under the repository no-skip policy.
  Status: pre-existing

## Acceptance Criteria Assessment

- The fixed-key singleton, persisted reminder, startup activation, four-table pull, type fan-out, retry, atomic poison settlement, origin-aware marking, and dead-letter API/CLI are implemented in `DispatcherGrain.cs`, `DispatcherActivationService.cs`, `EventStore.cs`, `EventDispatcherService.cs`, `DeadLetterStore.cs`, `DeadLetterRoutes.cs`, and `MohistCliCommands.Event.cs`.
- The focused dispatcher/dead-letter/Agent/inbox/CLI slices passed 105 tests. Full `npm test` also passed: CLI 873, server unit 1367, architecture 24 passed / 3 skipped, server spec 2843 passed / 9 skipped, Web 4596, and Runner 1007.
- The candidate does not meet the durable Agent recovery and configured-capacity acceptance criteria because items 1-3 remain open. Item 4 also allows a required downstream durable publish to be silently lost after a retry.

## Verification

- `git diff --check e594b8c4f^..HEAD` passed.
- Focused server unit, server spec, and CLI test slices passed.
- `npm test` passed.

<promise>FAIL</promise>
