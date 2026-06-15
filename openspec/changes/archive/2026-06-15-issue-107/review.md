# Review Report

## Result: PASS

## Repaired Items

None — this review is the post-repair snapshot after the previous review cycle applied all blocking fixes.

## Blocking Items

None. All previously blocking items have been addressed:

- item-1: `InfoCollector.cs:1064, 1073` — `State = NotInstalled` for missing systemd unit; verified by `Collect_SystemctlUnitMissing_HandlesFailSafe` and `RenderJson_ServiceNotInstalled_StateShowsNotInstalledSentinel`.
- item-2: `InfoResult.cs:19-28` adds `Connectivity`; `InfoCollector.cs:498-514` writes to it; `BuildServiceStatusJson` (line 283-301) emits separate `state`/`uptime`/`uptimeSeconds`/`connectivity` keys; `BuildServiceLine` (line 527-555) concatenates only for default text.
- item-3: `InfoCollector.cs:693-703` — accepts both `data.models` (array) and `data.model` (string) from the real server response.
- item-4: `CommandExecutor.cs:11-63` — `ICommandExecutor.ExecuteAsync` takes `CancellationToken`; `SystemCommandExecutor` registers a kill callback.
- item-5: `InfoCollector.cs:516-525` — `BuildCliLine` prefixes `v` and appends `(built <date>)`; `GetCliAsync` (line 1017-1043) preserves the `+<sha>` suffix and extracts the build date.
- item-6: `InfoCollector.cs:664, 720-731` — `MOHIST_AGENT_COMMAND` validated against `{opencode, opencode.exe}` allow-list; verified by `Verbose_OpencodeRuntime_RejectsUnknownCommand`.
- item-7: Spec scenarios for `<not installed>` vs `<unknown>` are now correctly distinguished (item-1 covers it).
- item-8: `InfoCollector.cs:1231-1245` — `IsSystemdAvailable` no longer uses the `XDG_RUNTIME_DIR` heuristic.
- item-15: `InfoCollectorSpecs.cs:349-373` — regression assertion for `<not installed>` is now present.

## Warning Items

- [ID: item-W1]
  Severity: warning
  Scope: `InfoCollector.cs:152-153` (`CollectVerboseAsync`)
  Evidence: `var isGitRepo = (sourcePath is not null) && (server.Source?.CommitShort is not null || runner.Source?.CommitShort is not null);` is computed at the start of `CollectVerboseAsync` but never read; the variable is dead.
  SuggestedAction: Delete the unused `isGitRepo` line.
  Verification: `dotnet build packages/cli/Mohist.Cli/Mohist.Cli.csproj` succeeds; all CliInfo tests still pass.
  Status: open

- [ID: item-W2]
  Severity: warning
  Scope: `InfoCollector.cs:23` and `InfoCollector.cs:527` (`BuildServiceLine`)
  Evidence: The `TimeoutSentinel = "<timeout>"` constant is declared but never referenced. The `BuildServiceLine(string, InfoService, bool includeSource)` parameter `includeSource` is declared but never used in the function body.
  SuggestedAction: Either use the sentinel in the slow-source path or remove it; either use `includeSource` to gate the source line or remove the parameter.
  Verification: `dotnet build` remains clean; tests unchanged.
  Status: open

- [ID: item-W3]
  Severity: warning
  Scope: `InfoCollector.cs:35-36, 88-89, 1207-1218` (`ResolveConnectivityUrl`)
  Evidence: The `_getServerBaseAddress` and `_getRunnerServerUrlOverride` `Func<string?>` fields are always set to `() => null` in the only constructor. `ResolveConnectivityUrl` always returns `/api/projects` (relative). The first two `if` branches are dead.
  SuggestedAction: Either wire the hooks into the production DI graph (e.g. accept the server base address from a configuration source) or remove them.
  Verification: `dotnet build` succeeds; behavior unchanged.
  Status: open

- [ID: item-W4]
  Severity: warning
  Scope: `InfoCollector.cs:1136-1167` (`GetProjectAsync`) and JSON `dataDir.size`
  Evidence: Live run on this machine: `du -sh /home/surac/.mohist` (36 GB) consistently takes > 2 s on a cold cache, so the 2 s per-collector timeout fires and `dataDir.size` is rendered as `<unknown>`. This is a fail-safe (per spec) but the issue body explicitly calls out `< 1 s` as a target and the live environment violates it. Other collectors are well under their budget.
  SuggestedAction: Compute the data dir size only when it can be done quickly (e.g. cap the walk depth, run `du` only for verbose mode, or accept a "size unknown in default mode" trade-off). Document the budget vs. real-data behaviour.
  Verification: `time ./packages/cli/Mohist.Cli/bin/Debug/net11.0/Mohist.Cli info` after a cold cache; expect < 1 s.
  Status: open

- [ID: item-W5]
  Severity: warning
  Scope: `InfoCollector.cs:527-555` (`BuildServiceLine`) and `MohistCliApi.cs:214-221`
  Evidence: For `service.Status is null` the spec implies `<not running>` but the implementation renders `<unknown>`. Also `ProjectStatePath` (production) reads `Environment.GetFolderPath(SpecialFolder.UserProfile)` directly and ignores the test-only `ProjectStatePathOverride` hook — the production path is not testable. The data dir `home` in `GetDataDirAsync` correctly uses `_environment`, but the project state path does not.
  SuggestedAction: (a) When `status is null`, fall back to `<not running>` if the unit was supposed to be managed (i.e. systemd available). (b) Inject `IEnvironmentVariableProvider` into `MohistCliApi` for the state path so tests don't need a test-only override.
  Verification: Add a test that asserts `<not running>` for `service.Status is null` when systemd is available.
  Status: open

- [ID: item-W6]
  Severity: warning
  Scope: `InfoCollector.cs:683-710` (`GetOpencodeRuntimeVerboseAsync`) — `ModelCount` semantics
  Evidence: The real server returns `data.model` (a single string). The CLI maps that to `modelCount = 1`. The spec wording is "available models count" and a `models: []` array length also reports `0`. A consumer can't distinguish "0 models configured" from "no answer". The two paths both produce an integer, but `modelCount = 1` is not a "count" — it is a presence indicator.
  SuggestedAction: Either (a) read the count from a future `data.models` array server-side, or (b) rename the field to `hasModel`/`isConfigured` so consumers don't read it as a count.
  Verification: With the current server response, the JSON should expose a count; the current `1` overstates the actual list size.
  Status: open

- [ID: item-W7]
  Severity: warning
  Scope: `InfoCollector.cs:573-580` (`BuildProjectLine`)
  Evidence: For `project != null && IssueCount = 0`, the spec scenario says "the project line displays `<no project>` or `(0 issues)` respectively", but the implementation renders `(0 issues, 0 active)`. The extra `, 0 active` is technically more informative but is a spec drift — the test for the `<no project>` case only checks `Name` is empty.
  SuggestedAction: Either trim the suffix when `IssueCount = 0`, or document the deviation in a code comment and update the spec.
  Verification: Add a `BuildProjectLine_ZeroIssues_FormatsAsSpec` test that asserts the rendered text.
  Status: open

## Follow-up Items

- [ID: item-F1]
  Severity: follow-up
  Scope: `InfoCollector.cs:659-718` (`GetOpencodeRuntimeVerboseAsync`) — hard-coded `opencode` allow-list
  Evidence: `ValidateAgentCommand` only allows `opencode` and `opencode.exe`. A user with a non-standard agent binary cannot use this command's opencode section, even though they may have a perfectly valid alternative (e.g. `claude-code`, `aider`). The security rationale (uncontrolled process spawn) is sound, but the allow-list is implicit and undocumented.
  SuggestedAction: Document the allow-list in `design.md` and the spec; consider a future `MOHIST_AGENT_COMMANDS` list.
  Status: follow-up

- [ID: item-F2]
  Severity: follow-up
  Scope: `InfoCollector.cs:140-179` (`CollectVerboseAsync`) and `BuildVerbose`
  Evidence: `BuildVerbose` (line 184-225) writes a leading blank `writer.WriteLine()` (line 186) and 8 sections; the verbose section count is fixed and the format is hand-formatted. A future "drill-down" or additional section will require touching both the collector and the renderer.
  SuggestedAction: Consider a small section descriptor table; not urgent.
  Status: follow-up

- [ID: item-F3]
  Severity: follow-up
  Scope: `InfoCollector.cs:1207-1229` (`CombineUrl`)
  Evidence: This helper exists but is only reachable via the dead `_getServerBaseAddress`/`_getRunnerServerUrlOverride` paths. If the production DI is ever wired to feed a real base URL, the helper will be live, but the test coverage is non-existent.
  SuggestedAction: When item-W3 is resolved, add a small test for `CombineUrl` covering the four slash-handling cases.
  Status: follow-up

- [ID: item-F4]
  Severity: follow-up
  Scope: `InfoCollector.cs:130-142` (`CollectAsync`)
  Evidence: The `server.Status is { State: "active" } && runner.Status is { State: "active" } && systemdAvailable` gate for the connectivity check means the HTTP probe fires only when both services are active. The spec scenario for "Server is not running" is satisfied; the "Runner is not running" case is not explicit but the implementation is more conservative than the spec requires. `Test_Collect_RunnerInactive_SkipsServerConnectivityHttpCall` covers this.
  SuggestedAction: No code change needed; document the rationale in the design.
  Status: follow-up

- [ID: item-F5]
  Severity: follow-up
  Scope: `InfoCollector.cs:659-680` and `ValidateAgentCommand`
  Evidence: `Path.GetFileName` is called on the raw env value. On Windows, `Path.GetFileName("/usr/bin/opencode")` returns `"opencode"`, which passes. A future contributor who adds a path-separator-sensitive check (e.g. `Path.DirectorySeparatorChar`) might inadvertently break this. The current code is consistent across platforms.
  SuggestedAction: No code change needed; add a brief comment in `ValidateAgentCommand` explaining the cross-platform intent.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-P1]
  Severity: pre-existing
  Scope: `packages/server/tests/Mohist.Server.Tests/Support/MohistIntegrationFixture.cs` and downstream
  Evidence: Running `dotnet test --filter "FullyQualifiedName~Specs.Api"` produces 104 pre-existing failures with `Microsoft.EntityFrameworkCore.Migrations.Internal.Migrator` errors about pending model changes. Unrelated to this change (no `Mohist.Server` migrations were modified by the candidate).
  SuggestedAction: Tracked separately under the database migration cleanup.
  Status: pre-existing

- [ID: item-P2]
  Severity: info
  Scope: `MohistCliApi.cs:214-221` and the `GetDataDirAsync` path
  Evidence: Two different ways to derive the home dir: `_environment.GetEnvironmentVariable("HOME")` (used by `GetDataDirAsync`) vs. `Environment.GetFolderPath(SpecialFolder.UserProfile)` (used by `ProjectStatePath`). On Linux they should agree, but on Windows the latter is the canonical UserProfile.
  SuggestedAction: Inject `IEnvironmentVariableProvider` into `MohistCliApi` and use `HOME` (or `USERPROFILE` on Windows) consistently.
  Status: out-of-scope

- [ID: item-P3]
  Severity: info
  Scope: Server `/api/opencode/runtime` response
  Evidence: The server's `OpencodeRoutes.cs` returns `{mode, command, model, note}` where `model` is a single string. The CLI's `data.models` fallback was added on the client side. The cleaner fix is to add a `models` array to the server response and feed it from `runnerRegistry.ListCoderModelsAsync()`.
  SuggestedAction: Server-side change; tracked separately.
  Status: out-of-scope

- [ID: item-P4]
  Severity: info
  Scope: `SkillAssetService` does not resolve an asset root in this environment
  Evidence: `mo info --verbose` renders the skills section as just `mohist` and `mohist-explore` (no install path) because the asset root is not found.
  SuggestedAction: Out of scope; teach `SkillAssetService` to fall back to `~/.hermes/skills`, `~/.agents/skills`, etc.
  Status: out-of-scope

## Verification

- `dotnet build packages/cli/Mohist.Cli/Mohist.Cli.csproj` — succeeds, 0 warnings, 0 errors.
- `dotnet test --filter "FullyQualifiedName~CliInfo|FullyQualifiedName~Skills"` — **148 / 148 passed** (73 CliInfo + 75 Skills).
- `dotnet run -- info` — renders 7 lines: CLI/Server/source/Runner/source/Project/Data dir, plus connectivity arrow. The Data-dir size flips between `36G` and `<unknown>` depending on whether `du` finishes inside its 2 s budget.
- `dotnet run -- info --json` — runner `state == "active"`, `connectivity == "server ok"`, `uptimeSeconds == 1270800`; opencode runtime `modelCount == 1` (from the live server's `model` field).
- `MOHIST_AGENT_COMMAND=cat dotnet run -- info --verbose` — `command: <unknown>`, `version: <unknown>`, `cat` is not invoked; validation works.
- All previously blocking items (1-15) are now resolved with both code change and a verifying test.
<promise>PASS</promise>
