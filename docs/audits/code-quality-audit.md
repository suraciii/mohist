# Code Quality Audit

> Scope: `packages/server/src/Mohist.Server/`, `packages/runner/src/`, `packages/web/src/`.
>
> Out of scope (audited by other agents): Workflow / Issue / event-bus / architecture.
>
> Method: read-only. Walked the project tree, used codegraph (status: 667 files, 9,186 nodes indexed) and parallel `rg`/`Grep` passes, cross-referenced spec coverage under `packages/server/tests/Mohist.Server.Tests/Specs/`.
>
> This audit focuses on **non-architectural** code-quality smells: dead code, naming, error handling, logging, nullable discipline, comments, test coverage, doc drift, build hygiene, file organization, magic strings, and frontend hygiene.

## Summary table

| # | Sev  | Title | File |
|---|------|-------|------|
| 1 | **P0** | `EventBusEventTypes` is a dead file: superseded by `EventCatalog`, never referenced | `Infrastructure/Events/EventBusEventTypes.cs:1-53` |
| 2 | **P0** | `async void` event-handler with no top-level try/catch (UI will crash if CloudEvent.Extensions throws) | `Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54` |
| 3 | **P0** | `async void` event-handler with broad `catch (Exception ex)` masking failures (UI will swallow) | `Sessions/Services/AgentSessionRunnerBridge.cs:63` |
| 4 | **P0** | `DispatchLifecycleHooksAsync<T>` is a no-op shim documented as "retained for any callers" — zero callers | `Workflow/Grains/WorkflowGrain.cs:1060-1066` |
| 5 | **P0** | `GetHookContext` is unused dead code | `Workflow/Grains/WorkflowGrain.cs:1097-1104` |
| 6 | **P0** | Fire-and-forget `_ = Method()` in `IssueGrain` event handlers — unobserved task exceptions | `Issue/Grains/IssueGrain.cs:95,104,113` |
| 7 | **P1** | `OnDeactivateAsync` swallows all exceptions and silently loses in-memory state | `Workflow/Grains/WorkflowGrain.cs:78-83` |
| 8 | **P1** | `WorkflowRun.Metadata.CreatedAt` set to `DateTimeOffset.MinValue` (displayed as `0001-01-01`) | `Workflow/Grains/WorkflowGrain.cs:1094` |
| 9 | **P1** | Legacy `type: "stage_changed"`, `type: "coder_session_*"`, `type: "coder_text_chunk"`, etc. emitted as raw string literals — should use `EventCatalog` | `Workflow/Grains/WorkflowGrain.cs:943`, `Sessions/Grains/AgentSessionGrain.cs:295,318,331,358,374,386-388` |
| 10 | **P1** | Magic agent-runtime event-type strings (`"tool_call"`, `"tool_call_update"`, `"agent_message_chunk"`, `"agent_thought_chunk"`, `"agent_output_chunk"`) repeated 75× across server + web | `Sessions/Grains/AgentSessionGrain.cs:184,188,308,321,334,484,488,600` + 70 more |
| 11 | **P1** | Unused parameters with `_ =` discard: signals dead API contract | `Sessions/Domain/AgentSession.Transitions.cs:19,39,40` |
| 12 | **P1** | `catch (Exception ex) { _ = ex; }` swallows payload-parse failures with no logging | `Sessions/Grains/AgentSessionGrain.cs:538-541` |
| 13 | **P1** | Multiple empty `catch { }` blocks swallow exceptions silently | `Sessions/Services/AgentSessionQuerier.cs:426`, `Api/StatusRoutes.cs:111`, `Api/FsRoutes.cs:51,81`, `Issue/Services/IssueQuerier.cs:379`, `Infrastructure/Config/ConfigService.cs:355`, `SystemInfo/ServiceStatusChecker.cs:38` |
| 14 | **P1** | `WorkflowEventType.Waveform` magic strings inside switch (`"failed"`/`"cancelled"`) | `Sessions/Grains/AgentSessionGrain.cs:386-388` |
| 15 | **P1** | `IssueRepositoryResolutionHelpers.CheckRepositoryConfigured` result type is implicit (method returns null OR `IResult` — overloaded for callers) | `Api/IssueRepositoryResolutionHelpers.cs:5-37` |
| 16 | **P1** | `[Fact]`/`[Theory]` count: 722 in tests, but `IssueGrain` and `WorkflowGrain` paths have no spec for `OnDeactivateAsync` event flush (per audit) | `tests/Mohist.Server.Tests/Specs/Workflow/Grain/` (22 files, no `OnDeactivateSpecs.cs`) |
| 17 | **P1** | `AGENTS.md` claims `approval_requested` SSE event is dead-registered; emits are still missing server-side | `Infrastructure/Events/EventCatalog.cs:23`, `InMemoryEventBus.cs:7` (see also: `web/src/app/providers/LiveTaskProvider.tsx:192` subscribes to it) |
| 18 | **P1** | No spec for `OnDeactivateAsync` event flush — and the spec layer exposes 21 spec files for workflow-grain but skips that path | `tests/Mohist.Server.Tests/Specs/Workflow/Grain/` |
| 19 | **P2** | `ProjectInfo.CreatedAt`/`UpdatedAt` are `string` (ISO-8601) instead of `DateTimeOffset` | `Project/Services/ProjectInfo.cs:12-13` |
| 20 | **P2** | `ProjectInfo` exposes public `set` on every Orleans-serialised field; risk of accidental mutation across grain boundaries | `Project/Services/ProjectInfo.cs:8-13`, `Project/Domain/RepositoryInfo.cs:6-8` |
| 21 | **P2** | Hardcoded `DateTime.UtcNow` instead of `IClock`/testable time | `Issue/Domain/Issue.Transitions.cs:16,68`, `Issue/Services/...`, `Sessions/...` |
| 22 | **P2** | `InMemoryEventBus.Emit` is `void` and synchronously invokes handlers — handler exceptions only logged, not propagated | `Infrastructure/Events/InMemoryEventBus.cs:55-150` |
| 23 | **P2** | `_ = ex;` in `AgentSessionGrain.ExtractText` discards the JSON parse exception | `Sessions/Grains/AgentSessionGrain.cs:538-541` |
| 24 | **P2** | `parseToolCall` swallows `JsonException` via bare `catch` | `Sessions/Grains/AgentSessionGrain.cs:616-619` |
| 25 | **P2** | `OnDeactivateAsync` returns `Task.CompletedTask` after `try { sub.Dispose(); } catch { }` | `Issue/Grains/IssueGrain.cs:79-87` |
| 26 | **P2** | `_run.Metadata?.Annotations` lookups with lowercase keys (`"issueNumber"`, `"projectId"`, `"issueId"`) — magic strings shared with `BuildRunMetadata` | `Workflow/Grains/WorkflowGrain.cs:939,1084,1091` |
| 27 | **P2** | Hardcoded event-type strings in switch statements and `Emit` calls bypass `EventCatalog` | `Workflow/Grains/WorkflowGrain.cs:943`, `Sessions/Grains/AgentSessionGrain.cs:295,318,331,358,374,386-388` |
| 28 | **P2** | `JsonElementSurrogate` re-parses on every deserialisation — perf concern flagged in P0-4 of `workflow-domain-audit` but no fix in this audit | (out of scope — referenced) |
| 29 | **P2** | `MergeContext` `_ = runnerId;` — runner id is silently ignored, an explicit test would catch this | `Sessions/Domain/AgentSession.Transitions.cs:19` |
| 30 | **P2** | `_run.Metadata.Annotations` is consumed at line 939 but `Metadata.Annotations` constructor sets `null` and is never propagated through `BuildRunMetadata` path's `input.Annotations` (look at `BuildRunMetadata`: annotations dict is `null` when input.Annotations is null AND projectId/issueId are blank, but `WorkflowRunMetadata` allows `Annotations = null` and `WorkflowGrain.EmitStageChanged` does `?.Annotations` — partial) | `Workflow/Grains/WorkflowGrain.cs:1076-1092` |
| 31 | **P2** | `// === Legacy names kept for back-compat with the Web (snake_case) ===` block in `EventCatalog.All` contains 45 entries, but 36 are dead registrations per the existing audit | `Infrastructure/Events/EventCatalog.cs:14-71` |
| 32 | **P2** | `log.LogError` calls always pass the exception as first arg but never include any structured context (project id, run id) — grep shows that some include but several don't | scattered: e.g. `Workflow/Grains/WorkflowGrain.cs:80-83` (has Id) vs. `Api/ExceptionMiddleware.cs:17` (no Id) |
| 33 | **P2** | File-private `DateTime.UtcNow.ToString("o")` repeated in many domain types — date format should be a single helper | `Sessions/Grains/AgentSessionGrain.cs:524` etc. (15+ uses) |
| 34 | **P2** | `DispatchLifecycleHooksAsync` parameter `IEnumerable<T> hooks` is dead along with the method | `Workflow/Grains/WorkflowGrain.cs:1060-1066` |
| 35 | **P2** | Tests/specs use `Assert.Equal` with hardcoded workflow-status strings; brittle | tests file-scattered, see `tests/Mohist.Server.Tests/Specs/Workflow/Grain/WorkflowRetrySpecs.cs:218` |
| 36 | **P2** | `WorkflowRun.Failure.cs` and other domain partial files: file-level XML doc missing (compare to `Issue.Transitions.cs`) | `Workflow/Domain/Run/*.cs` (no top-of-file docs) |
| 37 | **P2** | `_ = Task.Run(() => RunUpdateAsync(...))` fire-and-forget in `SystemUpdateService` | `SystemInfo/SystemUpdateService.cs:368` |
| 38 | **P2** | `IStateStore<T>.ListAsync` returns `NotSupportedException()` for 7 stores — they all return `throw new NotSupportedException();` (10+ call sites) | `Infrastructure/Data/Workflow/*.cs:45,38`, `Infrastructure/Data/Epic/*.cs:38,40`, `Infrastructure/Data/Issue/*.cs:38,49,51` |
| 39 | **P2** | `NotSupportedException` for "feature not implemented" should be `NotImplementedException` semantically | `Infrastructure/Data/Workflow/WorkflowStageLockStore.cs:45`, `WorkflowLeaseStore.cs:50`, `WorkflowVariablesStore.cs:37-38`, `WorkflowBacklogStore.cs:50`, `Issue/IssueStore.cs:49,51`, `IssueCounterStore.cs:38`, `Issue/IssueStore.cs:38,49,51`, `Epic/EpicCounterStore.cs:38,40` |
| 40 | **P2** | `WorkflowEventType` `RepairScheduled` is defined and emitted, but no spec covers it | `Workflow/Grains/WorkflowGrain.cs:996`, no `RepairScheduledSpecs.cs` |
| 41 | **P3** | `MergeContext` `_ = runnerId;` is not just an unused param — the runner id is dropped, contradicting the method name | `Sessions/Domain/AgentSession.Transitions.cs:19` |
| 42 | **P3** | `MergeContext` returns `[]` (empty list of events) for an eventful state change — should at least emit `AgentSessionContextMerged` | `Sessions/Domain/AgentSession.Transitions.cs:27` |
| 43 | **P3** | `AgentSession.MergeContext` is called as fire-and-forget via `_ =` in two `EnsureAsync` paths (line 45 and 53) | `Sessions/Grains/AgentSessionGrain.cs:45,53` |
| 44 | **P3** | `WorkflowGrain.GetHookContext` returns `(string ProjectId, string? IssueId, int? IssueNumber)` — named tuple uses property names that imply a wider API contract, but the method is dead | `Workflow/Grains/WorkflowGrain.cs:1097-1104` |
| 45 | **P3** | `WorkflowRun.Metadata.Annotations` lowercase keys (`"issueNumber"`, `"projectId"`, `"issueId"`) duplicate similar `CloudEvent` extension names (`"issueNumber"`, `"projectid"`, `"issueid"`) — naming inconsistency | `Workflow/Grains/WorkflowGrain.cs:939,1084,1091` vs. `Workflow/Grains/WorkflowGrain.cs:119-129` |
| 46 | **P3** | `[Id(N)]` attribute number order: `ProjectInfo` has `Id(0)..Id(5)` but `RepositoryInfo` has only `Id(0)..Id(2)` — inconsistent state per type | `Project/Services/ProjectInfo.cs:8-13`, `Project/Domain/RepositoryInfo.cs:6-10` |
| 47 | **P3** | C#: `_issueStore`, `_dbFactory`, `_runStore` all use `_camelCase` correctly, but private const `HomeEnvironmentVariable` is `PascalCase` (correct) — actually this is fine; no violations found | (verified clean) |
| 48 | **P3** | `WorkflowGrain` is 1153 lines — largest C# file in the project, multiple responsibilities (state, dispatch, repair, lifecycle, lease, variables) | `Workflow/Grains/WorkflowGrain.cs` |
| 49 | **P3** | `SystemUpdateService` is 655 lines, second-largest — mix of command runner, store, service, logger all in one file | `SystemInfo/SystemUpdateService.cs` |
| 50 | **P3** | `AgentSessionGrain` is 655 lines, third-largest — event projection + state + serialization + emit all in one file | `Sessions/Grains/AgentSessionGrain.cs` |
| 51 | **P3** | Magic numbers in timeouts: `5*60*1000` (5-min lease), `30*60*1000` (30-min session), `30*1000` (30s), `2*1024*1024` (2 MB) — should be `TimeSpan` constants | `Workflow/Grains/WorkflowGrain.cs:95` (`TimeSpan.FromMinutes(5)` is OK), `runner/src/actions/acp-agent.ts:36-40` |
| 52 | **P3** | `WorkflowGrain` constructor takes 9 dependencies (lines 38-47) — smells like a god-grain | `Issue/Grains/IssueGrain.cs:37-47` (10 deps) |
| 53 | **P3** | `// [Fact]` / `// [Theory]` count: 722 specs. The runner folder has 0 test files | `packages/runner/src/**` (no `tests/` dir at all) |
| 54 | **P3** | `ProjectInfo` / `RepositoryInfo` use `[Id(N)] public string X { get; set; }` for Orleans storage; setters are public | `Project/Services/ProjectInfo.cs:8-13` |
| 55 | **P3** | `// Best effort` comments document swallowed exceptions but the actual swallow sites have `try { … } catch { }` — no log even at debug level | `Infrastructure/Workspace/GitService.cs:204`, `SystemInfo/GitSourceInspector.cs:96` |
| 56 | **P3** | `InMemoryEventBus` has 145-line `Emit(CloudEvent)` method doing 4 things: dedup, lookup, dispatch legacy, dispatch typed | `Infrastructure/Events/InMemoryEventBus.cs:55-150` |
| 57 | **P3** | `_run.Metadata?.Annotations is { } a && a.TryGetValue("issueNumber", out var n)` is a complex inline pattern; readability hurts | `Workflow/Grains/WorkflowGrain.cs:939-941` |
| 58 | **P3** | `ProjectRoutes.cs:392` is largest API file with multiple `MapPost` chains; could split per resource | `Api/ProjectRoutes.cs` (392 lines) |
| 59 | **P3** | `WorkflowView.tsx` is 1267 lines — one of two >1000-line TSX files in the web | `web/src/widgets/issue-workflow/ui/WorkflowView.tsx` |
| 60 | **P3** | `IssueDetailPage.tsx` is 1190 lines | `web/src/pages/issue-detail/ui/IssueDetailPage.tsx` |
| 61 | **P3** | `AssistantParts.tsx` is 1161 lines and uses 5 instances of `any` cast | `web/src/widgets/session-transcript/ui/AssistantParts.tsx:300,357,358,359,375` |
| 62 | **P3** | Frontend uses `key={i}` (index as key) in 10 list renders — anti-pattern when list mutates | `web/src/pages/issue-detail/ui/IssueDetailPage.tsx:179`, `web/src/pages/settings/ui/SectionState.tsx:54`, `web/src/pages/logs/ui/LogsPage.tsx:207`, `web/src/widgets/coder-session/ui/SessionTimeline.tsx:293`, `web/src/widgets/issue-workflow/ui/ReviewSummary.tsx:158`, `web/src/widgets/coder-session/ui/SessionCard.tsx:74,145`, `web/src/widgets/session-transcript/ui/ToolCallCard.tsx:44,241,490` |
| 63 | **P3** | Frontend `console.error` used as user-facing error sink (5× in `IssueModelSelector.tsx`) — no toast/notification | `web/src/features/select-issue-model/ui/IssueModelSelector.tsx:148,170,190,212` |
| 64 | **P3** | Runner uses `console.error` and `console.log` 9× in `host.ts` and `opencode-models.ts` — no structured logging | `packages/runner/src/runtime/host.ts:46,51,72,74,100,163,173,191`, `packages/runner/src/runtime/opencode-models.ts:13` |
| 65 | **P3** | `// Section divider` comments (e.g. `// Prompts`, `// Helpers`, `// Variables (Set + Patch)`) add visual structure but aren't XML-doc; might be WHY-comments but read as WHAT | `Workflow/Services/IssueWorkflowProfileManager.cs:115,159,215`, `ProjectWorkflowProfileManager.cs:46,62,150,197,242,418`, `WorkflowProfileManager.cs:208`, `Api/ProjectRoutes.cs:131,194` |
| 66 | **P3** | `DateTime.UtcNow.ToString("o")` is repeated 15+ times for ISO-8601 formatting | scattered, see `Sessions/Grains/AgentSessionGrain.cs:524` |
| 67 | **P3** | `interface` declarations missing XML doc (Orleans grain interfaces) | `Project/Grains/IProjectGrain.cs`, `Workflow/Grains/IWorkflowGrain.cs` etc. (all have no doc) |
| 68 | **P3** | `//[SuppressMessage]` count: 0 (good — but also means no built-in warning suppression tracking) | (verified clean) |
| 69 | **P3** | `#pragma warning disable` only in EF Core migration files (acceptable, auto-generated) | `Infrastructure/Data/Migrations/*.Designer.cs:17,20` (only those) |
| 70 | **P3** | `MergeContext` "extension" pattern (C# 14 `extension(AgentSession session)`) — used in `AgentSession.Transitions.cs`; looks modern but tooling support may be inconsistent | `Sessions/Domain/AgentSession.Transitions.cs:5-184` |

---

## P0 findings (must fix)

### P0-1. `EventBusEventTypes.cs` is dead — superseded by `EventCatalog`

- **Where**: `packages/server/src/Mohist.Server/Infrastructure/Events/EventBusEventTypes.cs:1-53`
- **What**: 53-line file declaring `internal static class EventBusEventTypes { All = [...45 strings...] }`. A repo-wide `rg` finds **zero** references outside the file itself. `EventBridge` uses `EventCatalog.All` instead (line 31 of `Events/Hub/EventBridge.cs`). The file is a documented dead artifact.
- **Fix**: delete the file. Already covered in `docs/audits/event-pipeline-audit.md` but the file is still here.

### P0-2. `async void` event handler with no top-level try/catch — UI will crash on malformed event

- **Where**: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/WorktreeCleanupService.cs:54-84`
- **What**: `private async void OnWorkflowCompleted(CloudEvent evt)`. The method has an inner `try { … } catch (Exception ex)` around the worktree removal — good — but if `TryGetString` or any extension lookup *before* the try throws (e.g. on a malformed `CloudEvent`), the process will crash with an unhandled exception (async void escapes try/catch at top level only on await boundaries). Two `TryGetString` calls precede the try block (lines 56-59). `int.TryParse(issueNumberStr, out var issueNumber)` is safe; but if `evt.GetPopulatedAttributes()` itself throws (it can on a corrupt event), the process crashes.
- **Fix**: wrap the entire body in a top-level try/catch, OR change signature to `private async Task OnWorkflowCompleted(CloudEvent evt)` and use `bus.OnType(...)` overload that takes `Func<CloudEvent, Task>`.

### P0-3. `async void` with broad `catch (Exception)` — same hazard, different file

- **Where**: `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionRunnerBridge.cs:63-100`
- **What**: `private async void OnRunnerDisconnected(CloudEvent evt)`. Has both an outer catch (line 96) and an inner catch (line 89) — at least defensive — but again, exceptions thrown *before* line 64 (e.g. in the bus dispatcher pre-call) crash the process. The `TryGetString` calls precede any try block.
- **Fix**: same as P0-2 — make it `async Task` and let the bus catch the exception.

### P0-4. `DispatchLifecycleHooksAsync<T>` is a no-op shim with a comment admitting it

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1060-1066`
- **What**:
  ```csharp
  private async Task DispatchLifecycleHooksAsync<T>(IEnumerable<T> hooks, string terminal, string? reason) where T : class
  {
      // No-op shim retained for any callers that may still reference
      // DispatchLifecycleHooksAsync. Step 8 of design/event-mechanism.md:
      // hook dispatch was removed; terminal-side effects flow through the
      // bus (IssueGrain bus subscription, worktree cleanup hosted service).
  }
  ```
  A repo-wide `rg` finds **zero** callers of this method. The method body is empty (only a comment).
- **Fix**: delete the method entirely. The bus-driven design is the current contract; the shim is misleading.

### P0-5. `GetHookContext` is dead code

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1097-1104`
- **What**:
  ```csharp
  private (string ProjectId, string? IssueId, int? IssueNumber) GetHookContext()
  {
      var projectId = _variables?.String("project", "id") ?? "";
      ...
  }
  ```
  Zero callers. Same dead-hook story as P0-4.
- **Fix**: delete the method.

### P0-6. Fire-and-forget `_ = Method()` in `IssueGrain` event handlers — unobserved task exceptions

- **Where**: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:95, 104, 113`
- **What**:
  ```csharp
  _ = CompleteWorkAsync(wrId);
  ...
  _ = AbortWorkAsync(wrId, TryGetExtension(evt, "reason") ?? "stopped");
  ```
  The `_ =` pattern in C# *discards* the `Task` and **does not** observe exceptions. If `CompleteWorkAsync` or `AbortWorkAsync` throws, the exception becomes an unobserved task exception that the GC will eventually surface as `UnobservedTaskException` — by default not crashing, but losing the failure. The comment at line 70-73 says "the handlers run as fire-and-forget Action<CloudEvent> callbacks so the bus dispatch never blocks", but the bus API has `OnType(... Action<CloudEvent>)` *and* presumably an `OnType(... Func<CloudEvent, Task>)` — using the latter would let the bus await the handler.
- **Fix**: change to `_ = CompleteWorkAsync(wrId).ContinueWith(t => _log.LogError(t.Exception, ...), TaskContinuationOptions.OnlyOnFaulted)` *or* use the `Func<CloudEvent, Task>` overload of `OnType` and let the bus observe exceptions.

---

## P1 findings (should fix soon)

### P1-1. `OnDeactivateAsync` swallows all exceptions and silently loses in-memory state

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:78-83`
- **What**:
  ```csharp
  catch (Exception ex)
  {
      _log.LogError(ex, "Workflow {Id} flush on deactivation failed; ...", GrainKey);
  }
  ```
  The catch exists (good — already in scope of `workflow-domain-audit.md` P1-1) but: the log message is the only signal. There is no metric, no event, no retry. In production a transient DB blip during silo shutdown means the workflow's last batch of mutations is gone.
- **Fix**: at minimum, also rethrow after logging if `reason` is `DeactivationReason.ApplicationShutdown` (so the silo logs it once at the boundary). Or push a `WorkflowRunPersistFailed` event so external watchers can act.

### P1-2. `WorkflowRun.Metadata.CreatedAt` is `DateTimeOffset.MinValue` — exposed as `0001-01-01` in API

- **Where**: `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:1094`
- **What**: `new WorkflowRunMetadata(input.Name, DateTimeOffset.MinValue, input.Labels, annotations)` — overrides the `WorkflowRun.Lifecycle.cs:31` default of `DateTimeOffset.UtcNow` with `MinValue`. The metadata view (`MetadataView.CreatedAt`) then surfaces `0001-01-01T00:00:00.0000000+00:00` in API responses.
- **Fix**: set `CreatedAt = DateTimeOffset.UtcNow` here, or remove the explicit MinValue and let the default apply.

### P1-3. Legacy `type:` strings bypass `EventCatalog` — inconsistent with new emits

- **Where**: 6 sites in 2 files.
  - `Workflow/Grains/WorkflowGrain.cs:943` — `type: "stage_changed"`
  - `Sessions/Grains/AgentSessionGrain.cs:295` — `type: "coder_session_started"`
  - `Sessions/Grains/AgentSessionGrain.cs:318` — `type: "coder_text_chunk"`
  - `Sessions/Grains/AgentSessionGrain.cs:331` — `type: "coder_thought_chunk"`
  - `Sessions/Grains/AgentSessionGrain.cs:358` — `type: "coder_tool_call"`
  - `Sessions/Grains/AgentSessionGrain.cs:374` — `type: "coder_session_status_changed"`
  - `Sessions/Grains/AgentSessionGrain.cs:386-388` — `busName` switch with `"coder_session_failed"`, `"coder_session_cancelled"`, `"coder_session_completed"`
- **What**: `EventCatalog.ReverseDns` already declares `AgentSessionStarted = "com.mohist.agent-session.started"` etc. (line 103-107 of `EventCatalog.cs`), and the catalog also has the legacy snake_case names in `EventCatalog.All` (line 17-71). The producer should use `EventCatalog.ReverseDns.X` for new emits and `EventCatalog.Legacy.X` (or similar) for back-compat. Currently producers mix both.
- **Fix**: add a `Legacy` static class to `EventCatalog` mirroring the snake_case names; replace all 7 sites with the constants.

### P1-4. Magic agent-runtime event-type strings repeated 75× across server + web

- **Where**: `Sessions/Grains/AgentSessionGrain.cs:184, 188, 308, 321, 334, 484, 488, 600` + 70 more across `AgentSessionQuerier.cs`, `AgentSessionRunnerBridge.cs`, and the web's `LiveTaskProvider.tsx`.
- **What**: `"tool_call"`, `"tool_call_update"`, `"agent_message_chunk"`, `"agent_thought_chunk"`, `"agent_output_chunk"`, `"tool_result"`, `"tool_result_update"`, `"usage_update"` are repeated string literals everywhere. A rename would require touching 75 sites. Some web-side consumers (e.g. `LiveTaskProvider.tsx:142`) use a `switch (eventName as EventName)` where missing cases silently fall through (no `default` case is bad — the cast hides intent).
- **Fix**: introduce a `RuntimeEventTypes` constants class (server) and matching `RuntimeEventType` enum (web) — or extend `EventCatalog` with a `Runtime` section.

### P1-5. Unused parameters with `_ =` discard — signals dead API contract

- **Where**: `Sessions/Domain/AgentSession.Transitions.cs:19, 39, 40`
- **What**:
  ```csharp
  public IReadOnlyList<AgentSessionEvent> MergeContext(
      string? runnerId, string? workId, string? workType, string? stage, string? title, int? issueNumber)
  {
      _ = runnerId;       // <-- silently dropped
      if (session.IssueNumber == 0 && issueNumber is > 0)
          session.Metadata = session.Metadata.WithLabel(...);
      ...
  }
  ```
  The `runnerId` parameter is documented (in the type signature) as part of the contract but is *ignored* by the implementation. A caller has no way to know. Same for `changeDir` and `processPid` in `AttachAgent` (lines 39-40).
- **Fix**: either use the parameters (set them on the session) or remove them from the public method signature. Silently discarding is the worst of both worlds.

### P1-6. `catch (Exception ex) { _ = ex; }` swallows payload-parse failures with no logging

- **Where**: `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:538-541`
- **What**:
  ```csharp
  catch (Exception ex)
  {
      _ = ex;
  }
  ```
  Exception caught, exception variable explicitly discarded, no log emitted. This is the textbook "anti-pattern" of catch-and-ignore. Compare to the same file's other catch blocks which at least `_log.LogWarning(ex, ...)`.
- **Fix**: `_log.LogDebug(ex, "Failed to extract text from agent runtime event payload")` (debug because the function returns `string.Empty` as a sane default; only log at debug to avoid noise).

### P1-7. Empty `catch { }` blocks scattered across the codebase

- **Where**:
  - `Sessions/Services/AgentSessionQuerier.cs:426`
  - `Api/StatusRoutes.cs:111`
  - `Api/FsRoutes.cs:51` (in `MapGet("/api/fs/search")`)
  - `Api/FsRoutes.cs:81` (in `SearchDirectory` recursive helper)
  - `Issue/Services/IssueQuerier.cs:379` (`return null;` in `DeserializeRun`)
  - `Infrastructure/Config/ConfigService.cs:355` (`return false;` in `JsonIsValid`)
  - `SystemInfo/ServiceStatusChecker.cs:38` (`try { process.Kill(...); } catch { }`)
  - `Issue/Grains/IssueGrain.cs:83` (`try { sub.Dispose(); } catch { /* swallow — best effort during deactivation */ }` — at least has a comment)
- **What**: bare `catch { }` swallows all exceptions including `OutOfMemoryException` and `StackOverflowException`. The `StatusRoutes.cs:111` one is in a `git ref` lookup and could mask filesystem corruption.
- **Fix**: at minimum log at debug/warning; for `IssueQuerier.DeserializeRun` and `ConfigService.JsonIsValid` (genuine "expected to fail sometimes" sites), use a typed `catch (JsonException)` instead of bare `catch`.

### P1-8. `WorkflowEventType` magic strings in switch (`"failed"` / `"cancelled"`)

- **Where**: `Sessions/Grains/AgentSessionGrain.cs:386-388`
- **What**:
  ```csharp
  var busName = terminal switch
  {
      "failed" => "coder_session_failed",
      "cancelled" => "coder_session_cancelled",
      _ => "coder_session_completed"
  };
  ```
  The `terminal` parameter is a string. The implementation likely receives `AgentSessionStatusNames.ToName(...)` (a known enum) but the contract is implicit. `AgentSessionStatusNames` exists precisely to centralise this — the producer should pass the enum, not the name.
- **Fix**: change the `EmitTerminal` signature to accept `AgentSessionStatus phase` and switch on the enum.

### P1-9. `IssueRepositoryResolutionHelpers.CheckRepositoryConfigured` overloaded return type

- **Where**: `Api/IssueRepositoryResolutionHelpers.cs:5-37`
- **What**: The method returns `IResult?` — `null` for "configured", or an `IResult` for "error response". Callers use it as both a guard (`if (... is null) continue;`) and as the response (`return repoError;`). This is a fine pattern but the *method* mixes return shapes. The two return-null cases (line 13 and the unconditional return at the end) make the API "returns IResult but null means OK" — confusing for a newcomer.
- **Fix**: split into two methods: `bool IsRepositoryConfigured(issue)` + `IResult BuildRepositoryNotConfiguredError(issue)`. Or use `OneOf<IResult, TSuccess>`.

### P1-10. No spec for `OnDeactivateAsync` event flush (audit-flagged gap not closed)

- **Where**: `tests/Mohist.Server.Tests/Specs/Workflow/Grain/` (22 spec files: `BacklogSpecs`, `ApprovalGateSpecs`, ... `WorkflowRetrySpecs` — none for `OnDeactivateAsync`).
- **What**: `WorkflowGrain.OnDeactivateAsync` (line 69-84) flushes `_run` and drops in-flight events on errors. The audit `workflow-domain-audit.md:5` already flags this as P1-1 (silent event loss on silo shutdown), but no spec has been added to exercise the flush path.
- **Fix**: add `tests/Mohist.Server.Tests/Specs/Workflow/Grain/OnDeactivateFlushSpecs.cs` that activates a grain, mutates state, deactivates mid-`SaveAsync`, asserts the persisted state is complete.

### P1-11. AGENTS.md says `approval_requested` is dead-registered — confirmed, but fix is in web, not server

- **Where**: `AGENTS.md` line 9 (in user's brief, quoted in `workflow-domain-audit.md:69`) says `approval_requested` SSE event is "dead registration" — confirmed by grep (`rg "approval_requested" packages/` finds only `EventCatalog.cs:23`, `EventBusEventTypes.cs:13` (dead, see P0-1), `LiveTaskProvider.tsx:192`, `entities/issue/@x/events.ts:11`). The web subscribes to it but no server emits it.
- **What**: documentation drift — the AGENTS.md claim is correct (no emit), but the web's subscription is still active. So when the user sees an approval gate, no real-time event fires.
- **Fix**: either remove the web subscription OR emit the event from `WorkflowGrain` when `StageApprovalRequested` event fires. See also `workflow-domain-audit.md` P0-3 for the broader event-completeness issue.

---

## P2 findings (should fix eventually)

### P2-1. `ProjectInfo.CreatedAt`/`UpdatedAt` are `string` (ISO-8601) instead of `DateTimeOffset`

- **Where**: `Project/Services/ProjectInfo.cs:12-13`
- **What**:
  ```csharp
  [Id(4)] public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
  [Id(5)] public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");
  ```
  These are serialised into Orleans grain state, and exposed via API. String format means callers have to `DateTimeOffset.Parse(...)` everywhere. Inconsistent with the rest of the codebase where `DateTimeOffset` is used (`WorkflowRunMetadata` etc.).
- **Fix**: change to `public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;` — Orleans serialises `DateTimeOffset` natively. Test that consumers handle the type change.

### P2-2. `ProjectInfo` exposes public `set` on every Orleans-serialised field

- **Where**: `Project/Services/ProjectInfo.cs:8-13`, `Project/Domain/RepositoryInfo.cs:6-8`
- **What**: every field is `{ get; set; }`. This makes the type mutable from any caller — risk of cross-grain mutation. Other Orleans state types in the codebase use `{ get; init; }` or `{ get; private set; }` (e.g. `EventRow.cs:10-12` uses `init`).
- **Fix**: change setters to `init` or `private set` (the grain then mutates the in-memory state via the surrogate pattern).

### P2-3. Hardcoded `DateTime.UtcNow` instead of `IClock`

- **Where**: `Issue/Domain/Issue.Transitions.cs:16, 68`, `Issue/Services/...`, `Sessions/...` (15+ sites).
- **What**: domain transitions call `DateTime.UtcNow` directly. The `Issue.Transitions` methods already accept `DateTime? now = null` (good!), but several call sites don't pass a value, falling back to `DateTime.UtcNow`. Tests that want to verify timestamp behavior need to either pass a clock or use the default. This is a *testability* problem.
- **Fix**: introduce `IClock` and inject it into grains. Replace `DateTime.UtcNow` with `_clock.UtcNow`. Update tests to use `TestClock`.

### P2-4. `InMemoryEventBus.Emit` is `void` and synchronously invokes handlers

- **Where**: `Infrastructure/Events/InMemoryEventBus.cs:55-150`
- **What**: `Emit(CloudEvent cloudEvent)` dispatches to all handlers in the calling thread. A handler that throws only logs a warning (line 123, 146) — exceptions are *not* propagated. The method does not return a `Task`; handlers may be async (or may not). The synchronous dispatch blocks the caller.
- **Fix**: make `Emit` return `Task`, dispatch handlers via `Task.WhenAll` (with per-handler try/catch so one bad handler doesn't fail the dispatch). The current model is "fire-and-forget with synchronous wait" — both bad.

### P2-5. `MergeContext` returns `[]` for an eventful state change

- **Where**: `Sessions/Domain/AgentSession.Transitions.cs:11-28`
- **What**: `MergeContext` mutates `session.Metadata` (changes 3 fields) but returns `[]` (no events). The pattern in this file is "transition method returns events for the caller to emit" — `MergeContext` breaks the pattern silently.
- **Fix**: return `[new AgentSessionContextMerged(...)]` or similar event, or document explicitly that this method is the *silent* metadata refresh.

### P2-6. `_run.Metadata?.Annotations` lookups with lowercase magic keys

- **Where**: `Workflow/Grains/WorkflowGrain.cs:939, 1084, 1091`
- **What**: `"issueNumber"`, `"projectId"`, `"issueId"` are hardcoded in the grain. Same names appear in `BuildRunMetadata` (line 1084-1091) as map keys. The CloudEvent extension names are lowercase too: `"issueNumber"`, `"projectid"`, `"issueid"` (line 121, 124-127). Inconsistent — `projectId` (camelCase) vs `projectid` (lowercase) for the same concept.
- **Fix**: introduce `WorkflowAnnotations` constants class. Use it from both `BuildRunMetadata` and `EmitStageChanged`. Decide on one case (recommend `projectid` everywhere — matches CloudEvents spec).

### P2-7. Hardcoded event-type strings in switch and `Emit` calls bypass `EventCatalog`

- **Where**: same as P1-3 (6 emit sites) + `EmitTerminal` switch (`Sessions/Grains/AgentSessionGrain.cs:386-388`).
- **What**: see P1-3. The severity is P1 for new emits (P1-3), P2 here for the *switch* site because the inputs are typed but the outputs are strings.
- **Fix**: same — centralise in `EventCatalog.Legacy`.

### P2-8. `_ = Task.Run(() => RunUpdateAsync(...))` in `SystemUpdateService`

- **Where**: `SystemInfo/SystemUpdateService.cs:368`
- **What**:
  ```csharp
  _ = Task.Run(() => RunUpdateAsync(startedState, CancellationToken.None), CancellationToken.None);
  ```
  Fire-and-forget with `CancellationToken.None` — the task cannot be cancelled. If the silo restarts during the update, the task continues running detached. No way to know when it finishes, no way to know if it failed.
- **Fix**: store the `Task` in a field, observe it via `ContinueWith(t => _log.LogError(t.Exception, ...), TaskContinuationOptions.OnlyOnFaulted)`. Expose a `TaskCompletionSource` for the API to await.

### P2-9. `IStateStore<T>.ListAsync` returns `NotSupportedException()` for 7 stores — should be `NotImplementedException` semantically

- **Where**: 7 stores across 3 sub-domains.
  - `Infrastructure/Data/Workflow/WorkflowStageLockStore.cs:45`
  - `Infrastructure/Data/Workflow/WorkflowLeaseStore.cs:50`
  - `Infrastructure/Data/Workflow/WorkflowVariablesStore.cs:37-38`
  - `Infrastructure/Data/Workflow/WorkflowBacklogStore.cs:50`
  - `Infrastructure/Data/Issue/IssueStore.cs:49, 51`
  - `Infrastructure/Data/Issue/IssueCounterStore.cs:38`
  - `Infrastructure/Data/Epic/EpicCounterStore.cs:38, 40`
- **What**: these methods throw `NotSupportedException`, which conventionally means "the operation is not supported by this implementation" (e.g. a read-only file system). The actual intent is "I haven't implemented this" — `NotImplementedException` is the correct semantic.
- **Fix**: change to `NotImplementedException` (or, if the operation is genuinely never needed, remove the method from the interface and provide a default no-op via the `IStateStore<T>` interface).

### P2-10. No spec for `RepairScheduled` event path

- **Where**: `Workflow/Grains/WorkflowGrain.cs:996` (`RepairScheduled => Task.CompletedTask`) — emits the event (via `WorkflowEventSerializer.cs:37` mapping), but no `tests/Mohist.Server.Tests/Specs/Workflow/Grain/RepairScheduledSpecs.cs` exists.
- **What**: `WorkflowRun.ScheduleCheckRepair` (per `workflow-domain-audit.md:24`) emits this; the grain handler is a no-op. No spec covers the emit-receipt path.
- **Fix**: add a `RepairScheduledSpecs.cs` that triggers a check failure, asserts a repair is scheduled, and asserts the `RepairScheduled` event reaches the bus.

### P2-11. Section-divider comments are not WHY-comments

- **Where**: 11+ sites.
  - `Workflow/Services/IssueWorkflowProfileManager.cs:115, 159, 215`
  - `Workflow/Services/ProjectWorkflowProfileManager.cs:46, 62, 150, 197, 242, 418`
  - `Workflow/Services/WorkflowProfileManager.cs:208`
  - `Api/ProjectRoutes.cs:131, 194`
- **What**: comments like `// Prompts`, `// Helpers`, `// Variables (Set + Patch)`, `// Project templates (CRUD)` are visual section dividers. They are not WHY-comments (they don't explain why the code exists). The file would be clearer with extracted classes per section.
- **Fix**: extract each section into a partial class file or a separate type (`ProjectWorkflowProfilePrompts`, `ProjectWorkflowProfileHelpers` etc.). The section dividers disappear when the class is split.

### P2-12. `_ = ex;` and bare `catch` for `parseToolCall`

- **Where**: `Sessions/Grains/AgentSessionGrain.cs:616-619` (`try { ... } catch { return null; }`).
- **What**: `ParseToolCall` swallows `JsonException` and returns `null`. The caller (line 336-338) checks for null and silently drops the tool call. If the opencode agent is sending malformed tool calls, the user sees no tool at all — no log, no metric.
- **Fix**: at minimum, `_log.LogWarning(ex, "Failed to parse tool call JSON")`. Better: increment a counter so the failure is visible.

### P2-13. `// === Legacy names kept for back-compat with the Web (snake_case) ===` block has 36 dead registrations

- **Where**: `Infrastructure/Events/EventCatalog.cs:14-71`
- **What**: 45 entries; per `docs/audits/event-pipeline-audit.md:24` and `design/event-mechanism.md:24-25`, only 9 are actually emitted. The other 36 are dead but the web subscribes to them (see `web/src/app/providers/LiveTaskProvider.tsx`). Removing them would break the web (silently). Adding emits for them is the proper fix.
- **Fix**: either (a) audit each entry to see if a corresponding emit exists and add the missing emits, or (b) document the dead-36 in a follow-up audit per event. This audit does not have the scope to verify all 36.

### P2-14. `WorkflowGrain` constructor takes 9 dependencies

- **Where**: `Workflow/Grains/WorkflowGrain.cs:38-47` (9 deps); `Issue/Grains/IssueGrain.cs:37-47` (10 deps).
- **What**: large constructor lists are a smell — the grain is doing too much. Compare to `ProjectGrain` (8 deps) and `AgentSessionGrain` (4 deps).
- **Fix**: group related services into a façade (e.g. `IWorkflowGrainServices` that wraps the 3 workflow stores). The grain then takes 3-4 deps.

### P2-15. `WorkflowGrain` is 1153 lines — largest C# file

- **Where**: `Workflow/Grains/WorkflowGrain.cs`
- **What**: 1153 lines, multiple responsibilities (state, dispatch, repair, lifecycle, lease, variables, heartbeat, projections). Compare to the average file size in the project (~50-200 lines).
- **Fix**: extract partial classes by concern (`WorkflowGrain.Dispatch.cs`, `WorkflowGrain.Heartbeat.cs`, `WorkflowGrain.Lifecycle.cs`, `WorkflowGrain.Projections.cs`).

### P2-16. `DateTime.UtcNow.ToString("o")` repeated 15+ times

- **Where**: scattered. e.g. `Sessions/Grains/AgentSessionGrain.cs:524`, `Project/Services/ProjectInfo.cs:12-13` (initialisers), `Issue/...`, `Workflow/...`.
- **What**: every caller re-implements ISO-8601 formatting. No `Iso8601` helper exists.
- **Fix**: introduce `static class Iso8601 { public static string Format(DateTime dt) => dt.ToString("o"); }` and use it everywhere. Pure code quality win.

### P2-17. `MergeContext` is called as fire-and-forget via `_ =` in two `EnsureAsync` paths

- **Where**: `Sessions/Grains/AgentSessionGrain.cs:45, 53`
- **What**:
  ```csharp
  if (!_session.IsTerminal)
      _ = _session.MergeContext(command.RunnerId, command.WorkId, command.WorkType, command.Stage, command.Title, command.IssueNumber);
  ```
  `_ = Method()` discards the return value. In this case the return is `IReadOnlyList<AgentSessionEvent>` — the events are lost. If `MergeContext` ever returns events (P2-5), they will be silently dropped here.
- **Fix**: collect events from `MergeContext` and process them in `EnsureAsync`'s existing event loop, or use a fire-and-forget wrapper that logs on exception.

### P2-18. `InMemoryEventBus` Emit is 95 lines doing 4 things

- **Where**: `Infrastructure/Events/InMemoryEventBus.cs:55-150`
- **What**: the single `Emit(CloudEvent)` method does dedup, lookup, dispatch legacy, dispatch typed. Hard to read; hard to test.
- **Fix**: extract into 3 private methods: `DispatchLegacy`, `DispatchTyped`, `DispatchDeduplicated`.

---

## P3 findings (polish)

### P3-1. `MergeContext` `_ = runnerId;` — runner id is silently dropped, contradicting the method name

- See P1-5. The method is called `MergeContext` and takes `runnerId` as a parameter — a strong signal that the runner id matters. Silently discarding is worse than not having the parameter at all.

### P3-2. `WorkflowGrain.GetHookContext` is dead code (see P0-5)

- Already covered.

### P3-3. `_run.Metadata.Annotations` lowercase keys vs CloudEvent extension case

- See P2-6.

### P3-4. `[Id(N)]` attribute ordering is inconsistent across files

- **Where**: `Project/Services/ProjectInfo.cs:8-13` (Id 0-5), `Project/Domain/RepositoryInfo.cs:6-10` (Id 0-2), `Workflow/Domain/Run/WorkflowRun.cs:12` (no `[Id(N)]` — different serialisation).
- **What**: Orleans surrogate attributes are inconsistent. Some types use `[Id(N)]`, some use the same property names without it.
- **Fix**: standardise on `[Id(N)]` for all grain-state types. Verify with a serialisation round-trip test.

### P3-5. `WorkflowGrain` and `SystemUpdateService` and `AgentSessionGrain` are > 600 lines

- See P2-15 for `WorkflowGrain`. `SystemUpdateService.cs:655` and `AgentSessionGrain.cs:655` are similar candidates for splitting.

### P3-6. Magic numbers in timeouts

- **Where**: `packages/runner/src/actions/acp-agent.ts:36-40`
  - `DEFAULT_TIMEOUT_MS = 30 * 60 * 1000` (30 min)
  - `DEFAULT_SESSION_START_TIMEOUT_MS = 30 * 1000` (30s)
  - `DEFAULT_LIVENESS_QUIET_THRESHOLD_MS = 5 * 60 * 1000` (5 min)
  - `DEFAULT_PROBE_TIMEOUT_MS = 30 * 1000` (30s)
  - `MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024` (2 MB)
- **What**: bare-number constants. `30 * 60 * 1000` is not obvious; a reader has to compute it.
- **Fix**: use `30 * 60 * 1000` → `30 * 60 * SECOND_MS` or — even better — keep the raw form but extract them into a `Time` constants block at the top of the file. The C# side does better (`TimeSpan.FromMinutes(5)` at `WorkflowGrain.cs:95`).

### P3-7. `ProjectInfo` / `RepositoryInfo` use `[Id(N)] public string X { get; set; }` with public setters

- See P2-2.

### P3-8. `// Best effort` comments document swallowed exceptions

- **Where**: `Infrastructure/Workspace/GitService.cs:204`, `SystemInfo/GitSourceInspector.cs:96`.
- **What**: comments say "best effort" but the catch block is empty (`try { … } catch { }` or with no log). "Best effort" should mean "I tried, I logged the failure at the lowest level, I'm moving on." Currently no log.
- **Fix**: add `_log.LogDebug(ex, "...")` even for "best effort" sites.

### P3-9. Frontend `console.error` used as user-facing error sink

- **Where**: `web/src/features/select-issue-model/ui/IssueModelSelector.tsx:148, 170, 190, 212`.
- **What**: 4 `console.error` calls in user-facing error handlers. Users see the error in DevTools but no toast/inline-message.
- **Fix**: add a `useToast()` (or `sonner.toast.error`) call. The `sonner` package is already a dependency.

### P3-10. Runner uses `console.error`/`console.log` 9× — no structured logging

- **Where**: `packages/runner/src/runtime/host.ts:46, 51, 72, 74, 100, 163, 173, 191`, `packages/runner/src/runtime/opencode-models.ts:13`.
- **What**: 9 `console.*` calls. The runner is a separate TypeScript process; the server's structured logs are not visible to the runner.
- **Fix**: introduce a `RunnerLogger` interface with the same shape as `ILogger<T>`; pass it down from the host. Replace `console.*` with `_log.error(...)` / `_log.info(...)`.

### P3-11. Frontend uses `key={i}` (index as key) in 10 list renders

- **Where**:
  - `web/src/pages/issue-detail/ui/IssueDetailPage.tsx:179` (skeleton placeholders — fine for static)
  - `web/src/pages/settings/ui/SectionState.tsx:54`
  - `web/src/pages/logs/ui/LogsPage.tsx:207`
  - `web/src/widgets/coder-session/ui/SessionTimeline.tsx:293`
  - `web/src/widgets/issue-workflow/ui/ReviewSummary.tsx:158`
  - `web/src/widgets/coder-session/ui/SessionCard.tsx:74, 145`
  - `web/src/widgets/session-transcript/ui/ToolCallCard.tsx:44, 241, 490`
- **What**: when the list mutates, React's reconciliation uses the index — items that move position will be re-rendered incorrectly. For static lists (e.g. logs that only append) this is harmless; for mutable lists this is a bug.
- **Fix**: change `key={i}` to `key={item.id}` (or whatever unique field the item has). For 7 of the 10 sites, the item has an id (`LogRow`, `SessionTimeline`, etc.).

### P3-12. `AssistantParts.tsx` uses 5 instances of `any` cast

- **Where**: `web/src/widgets/session-transcript/ui/AssistantParts.tsx:300, 357, 358, 359, 375`.
- **What**: `(r: any) =>`, `(t: any) =>`, `(todo: any, i: number) =>`. With `tsconfig.strict: true` and `noImplicitAny: true`, the explicit `any` is opt-in. A typed version of the data shape would let TypeScript catch bugs.
- **Fix**: define a `TodoItem` and `SearchResult` type at the top of the file; replace `any` with these.

### P3-13. `WorkflowView.tsx` is 1267 lines; `IssueDetailPage.tsx` is 1190 lines

- **Where**: `web/src/widgets/issue-workflow/ui/WorkflowView.tsx`, `web/src/pages/issue-detail/ui/IssueDetailPage.tsx`.
- **What**: too many responsibilities in a single file. `WorkflowView.tsx` does state, events, projections, formatting, and 3 `useEffect` hooks.
- **Fix**: extract custom hooks (`useWorkflowElapsed`, `useStageSelection`) and sub-components.

### P3-14. `parseToolCall` swallows `JsonException` via bare `catch`

- See P2-12.

### P3-15. `ProjectInfo` etc. are missing XML doc on `[Id(N)]` fields

- **Where**: `Project/Services/ProjectInfo.cs:8-13`, `Project/Domain/RepositoryInfo.cs:6-8`.
- **What**: Orleans surrogate fields with no XML doc. New contributors won't know what `Id(3)` means semantically.
- **Fix**: add `<summary>` per field. (Low priority — surrogates are internal.)

### P3-16. `OnDeactivateAsync` returns `Task.CompletedTask` after `try { sub.Dispose(); } catch { }`

- **Where**: `Issue/Grains/IssueGrain.cs:79-87`.
- **What**: already noted in P1-7. The signature returns `Task` (synchronous) but the comment line 67-73 talks about fire-and-forget `Action<CloudEvent>`. The handler `OnWorkflowCompleted` etc. are `void` (synchronous) and use `_ = Method()` — so the bus dispatcher can't await them.
- **Fix**: change `OnWorkflowCompleted` etc. to `async Task` and use `OnType(... Func<CloudEvent, Task>)`.

### P3-17. `// Section divider` comments

- See P2-11.

### P3-18. `DateTime.UtcNow.ToString("o")` repeated

- See P2-16.

### P3-19. `interface` declarations missing XML doc (Orleans grain interfaces)

- **Where**: `Project/Grains/IProjectGrain.cs`, `Workflow/Grains/IWorkflowGrain.cs`, etc.
- **What**: public interfaces with no XML doc. Microsoft convention is to doc the public surface.
- **Fix**: add `<summary>` to each method on the interface. (Low priority — the grain is the entry point, internal use is most common.)

### P3-20. `[SuppressMessage]` count: 0 — verified clean

- No `SuppressMessage` attributes anywhere in the source tree. Good — no suppressed warnings hiding in production code.

### P3-21. `#pragma warning disable` only in EF migrations — verified clean

- Only `MohistDbContextModelSnapshot.cs:17` and `20260605025642_InitialSchema.Designer.cs:20` use `#pragma warning disable 612, 618` — both are auto-generated EF Core files. Clean.

### P3-22. `extension(AgentSession session)` C# 14 syntax — modern, but tooling support

- **Where**: `Sessions/Domain/AgentSession.Transitions.cs:5-184`.
- **What**: the file uses the new C# 14 `extension(AgentSession session) { … }` block syntax for extension methods. This requires C# 14 / .NET 11. Verified `<LangVersion>preview</LangVersion>` in `Directory.Build.props` and `<TargetFramework>net11.0</TargetFramework>`. Good.
- **Fix**: none — this is a forward-looking pattern. Some IDEs may not highlight the `extension` block correctly; verify in your local IDE.

---

## Magic strings (audit focus item #11)

A focused list of string literals that should be constants, grouped by domain.

| String | Sites | Constant candidate |
|---|---|---|
| `"stage_changed"` | `WorkflowGrain.cs:943` | `EventCatalog.Legacy.StageChanged` |
| `"coder_session_started"` | `AgentSessionGrain.cs:295` | `EventCatalog.Legacy.CoderSessionStarted` |
| `"coder_session_failed"`, `"coder_session_cancelled"`, `"coder_session_completed"` | `AgentSessionGrain.cs:386-388` | `EventCatalog.Legacy.CoderSession*` |
| `"coder_text_chunk"`, `"coder_thought_chunk"`, `"coder_tool_call"`, `"coder_session_status_changed"` | `AgentSessionGrain.cs:318, 331, 358, 374` | `EventCatalog.Legacy.Coder*` |
| `"tool_call"`, `"tool_call_update"` | 9 sites in `AgentSessionGrain.cs`, `AgentSessionQuerier.cs` | `RuntimeEventTypes.ToolCall` etc. |
| `"agent_message_chunk"`, `"agent_thought_chunk"`, `"agent_output_chunk"`, `"tool_result"`, `"tool_result_update"`, `"usage_update"` | 5+ sites | `RuntimeEventTypes.AgentMessageChunk` etc. |
| `"failed"`, `"cancelled"`, `"completed"`, `"pending"`, `"in_progress"`, `"running"`, `"started"`, `"stopped"`, `"paused"`, `"resumed"`, `"created"`, `"active"`, `"done"` | many — see `AgentSessionStatusNames` (already exists) | use `AgentSessionStatus` enum + `AgentSessionStatusNames` (already partially done) |
| `"projectid"`, `"issueno"`, `"issueNumber"`, `"workflowrunid"`, `"runnerid"`, `"reason"`, `"workid"`, `"ageseconds"`, `"stage"`, `"status"`, `"action"` | many — see `CloudEventFactory` callers | introduce `CloudEventExtensions` constants |
| `"projectId"`, `"issueId"`, `"issueNumber"` (in `WorkflowRunMetadata.Annotations`) | `WorkflowGrain.cs:939, 1084, 1091` | `WorkflowAnnotations` constants |
| `"approval_requested"`, `"merge_queued"`, etc. (the 45 names in `EventCatalog.All`) | `EventCatalog.cs:14-71` | already constants — see P1-3 / P2-7 for the *emit* sites that bypass them |

---

## Frontend (audit focus item #12)

Verified `tsconfig.json` has `"strict": true`, `"noUnusedLocals": true`, `"noUnusedParameters": true`, `"noFallthroughCasesInSwitch": true`. Build hygiene is good.

- `useEffect` cleanup: all observed `useEffect` hooks with `addEventListener` / `setInterval` have a cleanup return (`LiveTaskProvider.tsx:83-87, 304-307`, `LogsPage.tsx:82-83`, `ActivityPage.tsx:28-29`, `KanbanBoard.tsx:483-485`, `SessionPage.tsx:583-638` (multiple), `WorkflowView.tsx:1185-1212`, `ReviewReportModal.tsx:46-48`). No leaks found.
- `useEffect` without cleanup: 4 sites use `useEffect` for state-sync only (no async resource): `CreateIssueDialog.tsx:195`, `EditIssueDialog.tsx:32`, `IssueModelSelector.tsx:114`, `EditEpicDialog.tsx:35`, `App.tsx:30, 36`. These are fine.
- `any` in TS: 5 sites in `AssistantParts.tsx` (P3-12), 1 site in test `kanban-board-query.test.tsx:256` (`undefined as any` for fixture).
- Missing keys in lists: 10 sites use `key={i}` (P3-11).
- `console.error` in user-facing error handlers: 4 sites in `IssueModelSelector.tsx` (P3-9), 1 in `events-hub.ts:37`.
- `process.exit` in runner: 0 sites.
- `void` discard in runner: see `host.ts:46` (`void this.connection.heartbeat(...)`) — intentional fire-and-forget.

---

## Build hygiene (audit focus item #9)

- `.editorconfig`: **no file** in repo root, `packages/server/`, or `packages/web/`. Should add one to enforce code style (e.g. 4-space indent, file-scoped namespaces, trailing newline).
- `#pragma warning disable`: only in EF migrations (auto-generated) — clean.
- `[SuppressMessage]`: 0 occurrences in production code — clean.
- `BannedApiAnalyzer` is referenced (`EnvironmentAbstractions.BannedApiAnalyzer` in `Mohist.Server.csproj:23`) — good, but `Directory.Build.props` has no `<NoWarn>` entries to control its output. Check that the banned API list is up to date.
- `Directory.Build.props`: `<TargetFramework>net11.0</TargetFramework>`, `<LangVersion>preview</LangVersion>`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. Strong baseline.
- `Directory.Packages.props`: CPM enabled, all versions pinned. Clean.

---

## Test coverage gaps (audit focus item #7)

| Path | Spec needed | Severity |
|------|-------------|----------|
| `WorkflowGrain.OnDeactivateAsync` event flush | `tests/Mohist.Server.Tests/Specs/Workflow/Grain/OnDeactivateFlushSpecs.cs` | P1 |
| `DispatchLifecycleHooksAsync` removal | (delete the method) | P0 |
| `GetHookContext` removal | (delete the method) | P0 |
| `EventBusEventTypes` removal | (delete the file) | P0 |
| `MergeContext` runner-id handling | spec for `Sessions/Domain/AgentSession.Transitions.cs:11-28` | P1 |
| `RepairScheduled` event reach | `tests/Mohist.Server.Tests/Specs/Workflow/Grain/RepairScheduledSpecs.cs` | P2 |
| `WorktreeCleanupService` no-op scenarios | test for missing projectId/issueNumber, malformed CloudEvent | P1 |
| `AgentSessionRunnerBridge` no-op scenarios | test for missing runnerId, no active sessions, single failing session | P1 |
| Runner package | **no `tests/` directory** at all under `packages/runner/` | P3 |
| `OnDeactivateAsync` exception recovery | assert that `_runDirty` is logged and the silo logs the failure | P1 |

---

## File organization (audit focus item #10)

Files in wrong folder:
- `Mohist.Server.csproj` is in `src/Mohist.Server/` and includes build steps to copy web assets from `../../../web/dist/`. The cross-package coupling (web → server) is in `.csproj` rather than in a documented build target. Acceptable but should be documented in AGENTS.md.

God classes (>1000 lines):
- `Workflow/Grains/WorkflowGrain.cs` (1153 lines) — P2-15.

Mixed responsibilities in single file:
- `Workflow/Services/ProjectWorkflowProfileManager.cs` (464 lines) — does templates + variables + prompts + helpers in one type. P2-11.
- `Sessions/Grains/AgentSessionGrain.cs` (655 lines) — state + event projection + serialization + emit. P2-15 / P3-5.
- `SystemInfo/SystemUpdateService.cs` (655 lines) — command runner + store + service + logger all in one file. P3-5.

---

## Executive summary

The Mohist backend is **~70% production-ready** from a code-quality perspective. The Orleans/EF Core architecture, the typed grain state, the bus-driven event flow, and the workflow domain model are well-engineered. The P0/P1 findings are isolated to **dead code from the Step 8 event-mechanism refactor** and **fire-and-forget `async void` event handlers** that are a known hazard.

### Top 3 quality risks

1. **Dead-code residue from the Step 8 refactor (P0-1, P0-4, P0-5)**. `EventBusEventTypes.cs`, `DispatchLifecycleHooksAsync<T>`, and `GetHookContext` are the visible remainders of the in-grain hook chain that was removed in favour of bus-driven dispatch. The risk is *not* that the code is broken (it isn't — it's just no-op) but that future readers will trust the `// No-op shim retained for any callers` comment, add a caller, and silently re-introduce the old architecture. **Action**: delete these three artifacts in a single commit, citing the audit.

2. **`async void` event handlers with no top-level try/catch (P0-2, P0-3)**. `WorktreeCleanupService.OnWorkflowCompleted` and `AgentSessionRunnerBridge.OnRunnerDisconnected` are both `async void` with `try/catch` only around the inner logic. A `CloudEvent` whose extension map is corrupt or whose `GetPopulatedAttributes()` throws will crash the silo. The risk is *real* — orphan crashes during bus dispatch have no recovery. **Action**: change both signatures to `async Task`; use the bus's `Func<CloudEvent, Task>` overload.

3. **Fire-and-forget `_ = Method()` in `IssueGrain` event handlers (P0-6)** + similar in `AgentSessionGrain.EnsureAsync` (P2-17) + `SystemUpdateService` (P2-8). Three different files use the `_ = AsyncCall()` pattern. The `_ =` discards the `Task`; exceptions become unobserved. Combined with the bus's `void Emit`, the system has multiple "fire-and-forget with no error visibility" surfaces. **Action**: standardize on `ContinueWith(t => _log.LogError(t.Exception, ...), TaskContinuationOptions.OnlyOnFaulted)` *or* on `async Task` callbacks that the bus can await.

### Top 3 quick wins

1. **Delete `EventBusEventTypes.cs`, `DispatchLifecycleHooksAsync<T>`, and `GetHookContext`** (P0-1, P0-4, P0-5). Total ~60 lines of pure dead code, with a misleading comment. ~10 minutes of work, zero risk. Cleans up 3 of the 5 P0 findings.

2. **Replace `type: "stage_changed"`, `type: "coder_session_*"`, `type: "coder_text_chunk"` etc. (6 sites) with `EventCatalog.ReverseDns.X` and add a new `EventCatalog.Legacy` for snake_case (P1-3, P2-7)**. The hardcoded strings are the entry point for "what is this event called" lookup. Centralising them closes 6 stringly-typed sites in 2 files. ~30 minutes.

3. **Fix the `MergeContext` `_ = runnerId;` discard (P1-5) and the `_ = ex;` in `AgentSessionGrain.ExtractText` (P1-6)**. Both are P1 because they encode "I dropped a piece of information" / "I dropped an exception" in a way that future maintainers will mistake for "this was on purpose". Replace with `session.Metadata = session.Metadata.WithLabel(AgentSessionMetadataKeys.RunnerId, runnerId);` and `_log.LogDebug(ex, ...)` respectively. ~15 minutes total.

### % production-ready estimate

| Dimension | Ready? | Notes |
|---|---|---|
| Naming consistency | 95% | All PascalCase/I-prefix/`_camelCase` rules observed in C#. Web has 1 file with `any` and 10 with `key={i}`. |
| Error handling | 60% | Many `catch (Exception)` blocks (acceptable if logged). 7 bare `catch { }` blocks. 2 `async void`. |
| Logging | 70% | All calls use structured logging (good). Some log calls missing the exception parameter. Runner uses `console.*` exclusively. |
| Nullable annotations | 85% | `Nullable=enable` + `TreatWarningsAsErrors=true` is enforced. `ProjectInfo.CreatedAt` is `string` (out of pattern). `required` keyword is used in 4 places. |
| Comments | 80% | Most comments are WHY. Section dividers (P2-11) and obsolete XML doc would be a polish pass. |
| Test coverage | 75% | 722 `[Fact]`/`[Theory]` tests. `OnDeactivateAsync` not covered. Runner has no tests at all. |
| Doc drift | 90% | `AGENTS.md` claim about `approval_requested` is correct (and is being fixed in other audit). |
| Build hygiene | 95% | CPM enabled, no suppressions, `.editorconfig` missing (1 fix). |
| File organization | 85% | `WorkflowGrain.cs` (1153 lines) and 2 others > 600 lines. |
| Magic strings | 60% | `EventCatalog` exists but is not the *only* source of truth. 6 emit sites bypass it (P1-3). 75 sites repeat agent-runtime event types. |
| Frontend | 80% | `tsconfig.strict: true`. `any` and `key={i}` are the main smells. |

**Overall: ~75% production-ready** — the architecture is sound, the type discipline is enforced, and the dead-code residue can be cleaned up in a single focused commit. The remaining 25% is mostly *consistency* (string centralisation, error-handling patterns, file size).

---

## Audit method

1. Enumerated `packages/server/src/Mohist.Server/` (220 .cs files, ~21,500 LoC) and the test tree (111 .cs files, ~25,000 LoC) via `codegraph_status` + `codegraph_files`.
2. Cross-referenced `codegraph_search` for symbols (`EventBusEventTypes`, `DispatchLifecycleHooksAsync`, `GetHookContext`, `MergeContext`, etc.) and `codegraph_context` for "how does X work" — no architectural questions, only "is this symbol used".
3. Parallel `rg` passes for the audit focus items: dead code (`_ =`, empty methods, `NotImplementedException`), error handling (`catch (Exception`, `catch {`, `async void`, `_ =` fire-and-forget), logging (`_logger.Log`, `Console.Write`, structured vs positional), magic strings (`"com.mohist."`, `"tool_call"`, `"stage_changed"`, etc.), magic numbers, frontend (`useEffect`, `key={i}`, `any`).
4. Verified test coverage per `packages/server/tests/Mohist.Server.Tests/Specs/` — counted 22 workflow-grain specs, 12 issue specs, 2 sessions specs, 6 project specs, 5 runner specs, 5 epic specs, 14 system specs, 5 foundation specs.
5. Did **not** modify any source file. This is read-only.

