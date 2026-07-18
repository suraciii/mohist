# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `RecordingHttpHandler.cs` had a trailing newline in the pre-change baseline (`0a`); this change stripped it (`\ No newline at end of file` in the diff) while appending new helpers. Restored the trailing newline to match the existing file convention.
  Verification: `tail -c 1 packages/cli/tests/Mohist.Cli.Tests/Support/RecordingHttpHandler.cs | xxd -p` now reports `0a`; `dotnet build Mohist.sln --no-restore -p:SkipWebBuild=true` succeeds with 0 warnings.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: formatting
  Evidence: `MohistCliCommands.Event.cs` had a trailing newline in the pre-change baseline; this change stripped it. Restored.
  Verification: `tail -c 1 packages/cli/Mohist.Cli/MohistCliCommands.Event.cs | xxd -p` now reports `0a`; build succeeds.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: formatting
  Evidence: Nine newly-created `.cs` files in this change ended with `}` (no trailing newline): `NdjsonStream.cs`, `CliEventsTailCommandSpecs.cs`, `ProjectEventTailRoutes.cs`, `EventTailSource.cs`, `IEventTailSource.cs`, `CloudEventEventMatchInput.cs`, `ProjectEventTailApiSpecs.cs`, `InMemoryEventTailSource.cs`, `CloudEventEventMatchInputTests.cs`. The surrounding repo convention is to terminate files with a newline (verified by sampling `LogsRoutes.cs`, `EventBridge.cs`, `MohistCliCommands.cs`). Appended a trailing newline to each.
  Verification: Each file now reports `0a` as its last byte; `dotnet build Mohist.sln --no-restore -p:SkipWebBuild=true` succeeds with 0 warnings; `dotnet test` for unit/spec/CLI suites still passes.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/Matching/CloudEventEventMatchInput.cs:56-57`
  Evidence: `Has("type")` returns `!string.IsNullOrEmpty(_event.Type)`, which treats an empty `Type` as absent. This is inconsistent with the general principle in `specs/event-envelope-matching/spec.md` ("has() over a present-but-empty attribute SHALL return true") and with the sibling core-field branches (`source` returns `Source is not null`; `subject` returns `_hasSubject`). The spec scenarios only pin the present-but-empty case for `subject`, and `CloudEvent.Type` is conventionally non-empty, so the gap is not exercised today, but a future producer that emits an empty `type` would surprise operators used to the documented `has()` semantics.
  SuggestedAction: Decide whether to align `Has("type")` with `source`/`subject` (return `Type is not null` rather than non-empty), or document the divergence in the adapter's doc comment. Either way, add a unit assertion that pins the chosen behaviour for the empty-`Type` edge case so the contract does not drift silently.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Api/ProjectEventTailApiSpecs.cs` (`TailSession.Dispose`/`Open`)
  Evidence: `TailSession.Open` busy-waits on `Thread.Yield()` for `ActiveSubscriptionCount` to rise, and `Dispose`/`CancelAsync` block on `_handler?.GetAwaiter().GetResult()` / `_reader?.GetAwaiter().GetResult()`. In a 15+-run stress loop, 1 run of the broader `Event|Tail` filter produced an `EventsBeforeSubscription_AreNotReplayed` failure rooted at `TailSession.Dispose()` line 520 (the handler `.GetAwaiter().GetResult()` sync-over-async). The ProjectEventTailApiSpecs class itself passed 10/10 and 5/5 subsequent reruns, so the flake is rare and most likely surfaces under thread-pool pressure from parallel collections. The sync-over-async pattern and the tight `Thread.Yield()` loop both increase the surface for this pressure. The catch in `Dispose` only handles `OperationCanceledException`; a non-OCE fault would propagate as an unhandled test exception.
  SuggestedAction: Replace the `ActiveSubscriptionCount` spin with a `TaskCompletionSource` signal raised from `EventTailSource.Open` (or a deterministic test-side hook), and make `Dispose`/`CancelAsync` fully async (await the handler/reader tasks rather than `.GetAwaiter().GetResult()`). At minimum, broaden the `Dispose` catch to swallow non-OCE reader/handler exceptions so a faulting background task cannot tear down an otherwise-passing test.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.SpecTests/Specs/Api/ProjectEventTailApiSpecs.cs:375-483` (`TailSession`)
  Evidence: Per the `fix(tests): unblock ProjectEventTailApiSpecs streaming tests` commit, `TailSession` now invokes the internal `ProjectEventTailRoutes.HandleTailAsync` directly against a `DefaultHttpContext` + `System.IO.Pipelines.Pipe` rather than through HTTP, because `WebApplicationFactory<Program>.CreateClient()` is backed by TestServer which buffers streaming responses. The streaming success path is therefore not exercised end-to-end through the ASP.NET route table — only `InvalidMatch_Returns400WithStructuredLocationBeforeAnyStream` and `UnknownProject_Returns404` drive the HTTP route for the tail. The trade-off is documented in the test remarks and is reasonable given the TestServer limitation, but it means the route registration, `ProjectResolutionEndpointFilter` integration, and the `X-Accel-Buffering`/`Cache-Control` header emission for a successful stream are not covered by an automated test.
  SuggestedAction: When a Kestrel-backed factory (or a `ResponseHeadersRead` client seam) is available, add a single end-to-end streaming smoke test that asserts the route, filter, and response headers for one streamed envelope, so the HTTP plumbing for the success path is pinned alongside the direct-invocation spec tests.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/NdjsonStream.cs:80-130` (`TryReadBadRequestDiagnosticAsync` / `ReadErrorMessageAsync`)
  Evidence: When `TryReadBadRequestDiagnosticAsync` fails to parse the 400 body (e.g. an intermediary returns an HTML error page instead of the server's structured diagnostic), `ReadErrorMessageAsync` is called next and re-requests `response.Content.ReadAsStreamAsync`. For `HttpContent` subtypes whose `CreateContentReadStreamAsync` returns the same underlying stream on repeat calls (e.g. `StreamContent`), the second read can observe the position left by the first attempt or an already-disposed stream. The current code is resilient for the tested path (the fake returns `StringContent`, which hands out a fresh `MemoryStream` per call, and the production server always returns well-formed JSON for 400s), so there is no observed bug — but the second-read contract is fragile.
  SuggestedAction: Buffer the 400 body once (e.g. `await response.Content.ReadAsStringAsync(cancellationToken)` up front and parse from the string in both branches), so the two readers do not share a stream position.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Events/Hub/EventTailSource.cs`
  Evidence: `EventTailSource` is a process-local `[Subscription]` handler. A tail opened against one daemon sees only events delivered to that silo's activation; events fanned to a different silo are invisible. This is already documented in `design.md` ("Risks / Trade-offs") and called out as a known limitation, and the current deployment is single-daemon, so it is not a defect of this change.
  SuggestedAction: Revisit tail coverage when the dispatcher is sharded across silos (e.g. via an Orleans stream backing the tail channel).
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Events/Hub/EventBridge.cs` / `UserNotificationDispatcher.cs`
  Evidence: The Web hub's project gate remains deliberately permissive (falls back to type-only matching when the connection or event lacks `projectid`), as called out in `design.md` D4. The new `EventTailSource` adds a separate, strictly-isolated fan-out alongside it rather than reusing the shared gate, which is the correct placement for this issue. No change was made to the permissive gate.
  SuggestedAction: None for this issue.
  Status: pre-existing

## Coverage summary (issue acceptance criteria → evidence)

- **`mo events tail --match <expr>` only outputs matching events.**
  - Server filter: `WithMatch_DeliversOnlyMatchingEnvelopesAndSuppressesNonMatches` (`ProjectEventTailApiSpecs.cs:71`) pushes four events with `match = "event.type == \"com.mohist.issue.completed\""` and asserts only the single matching envelope is delivered.
  - CLI filter forwarding + stdout: `Tail_WithMatch_ForwardsMatchExpressionAndStreamsFilter` (`CliEventsTailCommandSpecs.cs:56`) asserts the `?match=` query is forwarded verbatim (`event.type%20%3D%3D%20...`) and the matching line is printed.

- **All operators/functions with conformance covering syntax, missing attributes, regex timeout.**
  - 29 conformance tests in `EventMatchExpressionTests.cs`: precedence (`AndBindsTighterThanOr`, `ParenthesesOverridePrecedence`, `NotNegatesPresence`), `==`/`!=`/`in`/empty list (`EqualityInequalityAndMembershipAreOrdinal`), `startsWith`/`endsWith`/`contains`/`matches` (`StringFunctionsAreOrdinal`), literal rejection (`NonStringLiteralsAreRejected` for numeric/boolean/null), missing-attr semantics (`MissingAttributesBehaveAsEmptyStrings`), `has()` (`HasDistinguishesAbsentPresentAndPresentEmpty`), `event.data` rejection (`EventDataIsRejected`), eager regex rejection with location (`InvalidRegexReportsArgumentLocation`, offset 19), regex timeout as non-match with injected timeout (`RegexTimeoutIsNononMatch` via `TimeSpan.FromTicks(1)`), runtime failure as non-match through `IEventMatchFailureSink` (`RuntimeFailureIsRecordedAndDoesNotPropagate`, `FailureSinkFailureDoesNotPropagate`), determinism over 1000 evaluations (`RepeatedEvaluationIsStable`). All 29 pass in ~22ms.
  - `CloudEventEventMatchInputTests.cs` pins the adapter: core fields, extension resolution, missing→empty, null vs empty `subject`, present-but-empty extension vs absent, envelope-only matching.

- **Expression syntax errors rejected at submission with location.**
  - Compile-time location: `SyntaxErrorReportsOffsetLineAndColumn` (`EventMatchExpressionTests.cs:149`) compiles a multi-line unbalanced-paren expression and asserts line 2, positive column, positive offset.
  - Server 400-with-location: `InvalidMatch_Returns400WithStructuredLocationBeforeAnyStream` (`ProjectEventTailApiSpecs.cs:184`) asserts HTTP 400, `application/json`, `code = invalid_match_expression`, positive offset/line/column, and the source string echoed back — emitted before any stream line.
  - CLI stderr + non-zero exit before streaming: `Tail_InvalidMatch_PrintsLocationToStderrAndExitsNonZeroBeforeStreaming` (`CliEventsTailCommandSpecs.cs:89`) asserts non-zero exit, stderr contains the message + `line 1` + `column 20`, and stdout is empty.

- **Missing attributes compare as empty; `has()` distinguishes.**
  - `MissingAttributesBehaveAsEmptyStrings`: `event.epic == ""` matches, `event.epic.startsWith("7")` does not, `event.stage in ["plan","build"]` does not — all against an envelope with no `epic`/`stage` extension.
  - `HasDistinguishesAbsentPresentAndPresentEmpty`: `has(event.epic)` false on absent, true on `epic="7"`; `has(event.subject) && event.subject == ""` true for present-empty, false for absent.

- **Expression cannot access event payload.**
  - `EventDataIsRejected` pins compile-time rejection of `event.data == "x"` and `event.data.status == "failed"`, with the diagnostic containing `event.data`.
  - `Match_DoesNotConsultPayload` (`ProjectEventTailApiSpecs.cs:144`) publishes an event whose `data` would match the expression if payload were consulted but whose envelope `type` does not, and asserts the event is suppressed while a genuine envelope match is delivered. `EachLineCarriesEnvelopeFieldsAndExtensionsWithoutPayload` asserts the emitted JSON has neither `data` nor `payload`.

- **`mo events` consolidation (dead-letter relocation + singular removal).**
  - `LegacyEventNoun_FailsToResolveAndExitsNonZero` (`CliRootCommandShapeTests.cs:181`) asserts `mo event dead-letter list` no longer resolves and issues no HTTP request.
  - `Tail_SingularNoun_DoesNotResolve` (`CliEventsTailCommandSpecs.cs:226`) asserts `mo event tail` exits non-zero with no request.
  - Existing dead-letter specs retargeted to `events dead-letter` (`CliEventDeadLetterCommandSpecs.cs` diff: 10 command invocations changed from `event` to `events`). `docs/cli-reference.md` updated to `mo events` / `mo events tail` / `mo events dead-letter`.

## Verification

- `dotnet build Mohist.sln --no-restore -p:SkipWebBuild=true`: 0 warnings, 0 errors.
- `dotnet test packages/server/tests/Mohist.Server.UnitTests` — 1494 passed, 0 failed.
- `dotnet test packages/server/tests/Mohist.Server.ArchTests` — 27 passed, 0 failed.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests` — 942 passed, 0 failed.
- `dotnet test packages/server/tests/Mohist.Server.SpecTests --filter FullyQualifiedName~ProjectEventTailApiSpecs` — 11 passed, 0 failed (~700ms; previously hung at 1m40s per streaming test before the `HandleTailAsync` seam was introduced).
- Broader `SpecTests` sweep with `FullyQualifiedName~Event|FullyQualifiedName~Tail`: 485 passed, 0 failed across three consecutive runs (one earlier run had a single `EventsBeforeSubscription_AreNotReplayed` flake at `Dispose` — see follow-up item-5).

<promise>PASS</promise>
