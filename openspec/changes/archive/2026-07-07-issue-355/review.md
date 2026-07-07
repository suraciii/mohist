# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Notify.cs:219`
  Evidence: A second, independent `StripJsoncComments` implementation still exists in the CLI notify command. It is documented as "mirroring the server's `MohistConfigurationExtensions.StripJsoncComments`", but that server method has been deleted because `JsonNode.Parse` (with `JsonDocumentOptions { CommentHandling = Skip }`) already handles JSONC natively. The CLI copy is therefore redundant for the same reason and could be migrated to native parsing, though it is not on the server config-load path.
  SuggestedAction: Migrate `MohistCliCommands.Notify.cs` `LoadHermesConfig` to `JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })` and remove the CLI `StripJsoncComments` copy. Add/update CLI unit tests for commented input if any exist.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Config/MohistConfigurationExtensions.cs:39-40`
  Evidence: The `OnLoadException` handler logs `'ctx.Provider'` rather than the in-scope `configPath`. `ctx.Provider.ToString()` typically expands to a verbose string such as `"JsonConfigurationProvider for 'config.jsonc' (FileProvider: 'PhysicalFileProvider')"`, making the warning harder to scan than using the known absolute path.
  SuggestedAction: Change the interpolated string to use `{configPath}` instead of `{ctx.Provider}`.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Config/ConfigHotReloadSpecs.cs`
  Evidence: The hot-reload spec validates the options chain (`IConfigurationRoot.Reload()` → `IOptionsSnapshot<CleanupPolicyOptions>`) in a standalone harness. It does not exercise the actual `GET /api/runner/{runnerId}/config` HTTP endpoint, so there is no end-to-end proof that the handler returns the updated value on the wire after a reload.
  SuggestedAction: Once a stable `WebApplicationFactory`/`ConfigureAppConfiguration` harness is available, add an HTTP-level case that rewrites the temp JSONC source, calls `IConfigurationRoot.Reload()`, and asserts the endpoint response carries the new `cleanupPolicy.storageBudgetBytes`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: Full server test suite (`Mohist.Server.Tests`)
  Evidence: Running the full `Mohist.Server.Tests` assembly in this environment fails 17 cases in `LogsRouteSpecs` and `WorkflowArtifact*RouteSpecs` with `System.IO.IOException : No space left on device` under `/tmp`. The focused suite for issue-355 (`ConfigHotReloadSpecs`, `RunnerConfigApiSpecs`, `ConfigServiceSpecs`, `MohistConfigurationExtensionsSpecs`) passes 69/69, and the build is clean.
  SuggestedAction: Free `/tmp` space and rerun; failures are environmental, not caused by this change.
  Status: out-of-scope

- [ID: item-5]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:136-138`
  Evidence: The comment "`/poll` no longer carries CleanupPolicy (T-002 removed the field from WorkDispatchResponse atomically with the runner switch to /config)" references a different issue's T-002 (likely issue-359), which is confusing now that issue-355 also has a T-002. The comment was present before this change.
  SuggestedAction: Update the comment to reference issue-359 explicitly.
  Status: pre-existing

- [ID: item-6]
  Severity: warning
  Scope: `openspec/changes/issue-355/specs/config-hot-reload/spec.md:58-60`
  Evidence: The spec states: "The fault-tolerance semantics of the prior `AddJsonStream` + try/catch path MUST be preserved". This is factually incorrect — the prior `AddMohistConfigFile` implementation had no try/catch and a malformed `config.jsonc` aborted startup. The design (`design.md`) and proposal (`proposal.md`) correctly treat the new fault tolerance as a behavior change, not a preservation. The stale wording creates a traceability risk if someone later audits why `OnLoadException` was introduced.
  SuggestedAction: Align the spec sentence with the design/proposal: state that the new source introduces non-fatal load/reload failure handling because the prior path crashed on malformed input.
  Status: pre-existing

<promise>PASS</promise>
