# Review — issue-488 (`mo agent install supervisor`) — re-review after fixes

Reviewer role: independent review of the change as it sits in the tree now,
against `proposal.md`, `specs/agent-preset-install/spec.md`, `design.md`,
`tasks.json`, and the issue acceptance. No file other than this one was
modified. This re-review judges the current state after the prior round's
fixes landed; each finding carries enough context for a separate fix task.

## How the change was verified

- Read the spec and every changed product source (`PresetCatalog.cs`,
  `PresetAssetRootResolver.cs`, `ManagedAssetSynchronizer.cs`,
  `MohistCliCommands.Agent.cs`, `AgentInstallPreflight.cs`,
  `Update/UpdateOperations.cs`, `Mohist.Cli.csproj`), the preset resources
  (`presets/manifest.json`, `presets/supervisor/*.md`), and the test files
  (`CliAgentCommandSpecs.cs`, `ManagedAssetSynchronizerTests.cs`,
  `PresetCatalogTests.cs`, `UpdateInstallSyncTests.cs`, `UpdateTestFactory.cs`).
- `dotnet build Mohist.sln` clean (TreatWarningsAsErrors satisfied).
- `dotnet test Mohist.sln` green: Workflow.Definition 175, Cli 1371,
  Server.Unit 1346, Arch 32, Spec 3006.
- Empirically ran the built CLI in a simulated post-`mo update` managed-cache
  state: `mo agent install acme` prints
  `Unknown preset 'acme'. Available presets: supervisor.` (catalog resolves
  `supervisor` from `~/.mohist/cli/presets`). The prior round's headline
  failure (`Available presets: .`, feature dead) is gone.

## Prior-round findings — resolution status

The critical blocker from the prior review is properly fixed and verified:

- **F1 (deployment-path break) — FIXED.** `mo update` now syncs `presets/`
  alongside `skill-data/` (`UpdateOperations.cs:150-152,164,239-244`), and
  `PresetCatalog` resolves its root independently via `PresetAssetRootResolver`
  (managed cache → sibling), no longer deriving it from the skill-data root.
  Empirically confirmed above; covered by `PresetCatalogTests`
  (`CreateDefault_ResolvesManagedCacheLayout…`,
  `CreateDefault_WhenPresetsAbsentEverywhere_ListsNoPresets`),
  `ManagedAssetSynchronizerPresetTests`, and the command-level
  `AgentInstall_WhenManagedPresetsAbsent_ExitsNonZeroBeforeAnyHttp`.
- **F2 (preflight probed CWD but called it "the workspace") — ADDRESSED.**
  The project API exposes no per-repository workspace path (control/execution
  plane separation), so the warning is now framed honestly as a CWD-as-proxy
  check that names the resolved default repo (`AgentInstallPreflight.cs:17-48`,
  `DefaultRepository` struct).
- **F3 (rules' agentId binding untested) — ADDRESSED.**
  `AgentInstall_CreatesAgentAndRulesInOrder` now asserts both rule POST bodies
  bind `agentId` to `agent_supervisor`.
- **F4 (static PresetCatalogOverride) — ADDRESSED.** Removed; the install
  command builds the catalog from `api.FileSystem` + `api.GetUserHome`, and
  specs seed the managed-cache path to exercise the real resolution.

## Findings

### N1 — Agent 409-conflict safety net is broken (malformed re-resolve URL) [Medium, must fix]

The spec's idempotency requirement and design D3 both mandate a 409 safety net
for the concurrent-install race: if the list-then-create window is crossed by
another install, the resulting `AGENT_NAME_CONFLICT` (409) must be caught and
treated as "exists, skipped" — never as an error. The implementation catches
the 409 but then fails to re-resolve the agent, so the race path errors out
with contradictory output.

Location: `packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:173-178`

```csharp
if (response.StatusCode == HttpStatusCode.Conflict)
{
    api.Output.WriteLine($"exists, skipped: agent {preset.Name}");
    return await ResolveAgentAsync(api, path[..path.LastIndexOf("/agents", StringComparison.Ordinal)], preset.Name);
}
```

`EnsureAgentAsync` receives `path` = `ProjectAgentsPath(projectId, "/agents")`
= `/api/projects/{projectId}/agents` (caller at line 73-74). The slice
`path[..path.LastIndexOf("/agents")]` therefore yields `/api/projects/{projectId}`
— a **URL path**, not a project id. That string is passed as the `projectId`
argument to `ResolveAgentAsync`, which internally calls
`ProjectAgentsPath(projectId, "/agents?all=true")`, building:

```
/api/projects/%2Fapi%2Fprojects%2FprojectId/agents?all=true
```

i.e. a double-prefixed, escaped, nonexistent endpoint. The GET 404s, the list
is not a `JsonArray`, so `ResolveAgentAsync` prints `Agent 'supervisor' not
found` and returns null. `EnsureAgentAsync` returns null, install exits 1.

Net behavior under a concurrent-install race: the user sees
`exists, skipped: agent supervisor` followed by `Agent 'supervisor' not found`
and a non-zero exit — the safety net that D3 says must "捕获并按已存在跳过处理，
不报错" (catch and treat as exists/skip, do not error) instead errors out.

The rule-side 409 path (`EnsureRuleAsync`, line 229-233) is correct: it just
prints "exists, skipped" and returns true without re-resolving. Only the agent
side is broken, because it additionally tries to return the existing agent's
`AgentRef` (needed so the rules can bind to its id).

Coverage gap: there is **no test** for the agent 409-skip path. The existing
409 tests (`AgentCreate_MissingFieldsAndConflictFailClearly`,
`AgentUpdate_ConflictFailsClearly`) cover `create`/`update`, which correctly
surface 409 as an error; none exercise install's skip-on-409 branch.

Recommended fix (for the fix task):
- Thread the real `projectId` into `EnsureAgentAsync` (the caller at line 73-76
  already has `resolution.ProjectId`) and pass it to `ResolveAgentAsync`
  instead of slicing the path. Drop the `path.LastIndexOf("/agents")` trick.
- Add a spec: agent POST returns 409 Conflict (after the list returns empty,
  simulating the race) → install exits 0, prints `exists, skipped: agent
  supervisor`, and the subsequent rule POSTs bind `agentId` to the re-resolved
  supervisor agent (so `ResolveAgentAsync` must be made to succeed — e.g. the
  handler returns the agent on the list GET that follows the 409).

### N2 — `ManagedAssetSynchronizer.TryValidatePrepared` hardcodes the error message by label [Low, nit]

`ManagedAssetSynchronizer.cs:128-134` data-drives validation through the
`ManagedAssetKind` descriptor (skill → `*/SKILL.md`, preset → `manifest.json`)
but then picks the error string by comparing `kind.Label ==
ManagedAssetKind.Skill.Label` rather than carrying the message on the kind.
Works for the two current kinds; a third asset kind would get a wrong
("manifest.json not found") error label. Move the validation-error noun onto
`ManagedAssetKind` (next to `PreparedValidator`) so the descriptor stays the
single source.

### N3 — Three spec scenarios are covered only by composition, not pinned [Low, coverage]

The implementation is correct for all three, but no test would catch a
regression of the specific behavior:

- **Partial pre-existence** ("an Agent named `supervisor` and a
  `supervisor-approval` rule already exist, no `supervisor-failure`"): only
  the all-pre-existing (`AgentInstall_RerunSkipsExistingResources`) and
  none-pre-existing (`AgentInstall_CreatesAgentAndRulesInOrder`) ends are
  tested. A test where the agent + approval pre-exist but failure does not
  would pin "only the missing rule is created."
- **Verbatim placeholders in the POST body**: `{{event.*}}` preservation is
  asserted at the catalog level (`PresetCatalogTests`) but not on the
  `responsePrompt` actually POSTed by the command. The command passes
  `rule.ResponsePrompt` through verbatim (`MohistCliCommands.Agent.cs:220`), so
  the property holds, but an accidental future sanitization step would not be
  caught at the HTTP boundary.
- **Tail-append without an anchor**: `AgentInstall_CreatesAgentAndRulesInOrder`
  asserts request order and `continue == null` but not the **absence** of
  `before`/`after` in the rule POST body. A regression that added an anchor
  (and thus reordered rules) would slip through. Asserting the body has no
  `before`/`after` keys would pin "append at tail, no anchor."

## Positive notes

- The headline deployment-path defect is properly fixed and verified
  empirically; preset resolution is independent of skill-data per design D2,
  and `mo update` keeps the managed preset cache in sync.
- The generalized `ManagedAssetSynchronizer` is clean: the atomic temp+swap is
  reused, validation is data-driven (modulo the N2 nit), and preset sync
  aborts the update on a malformed bundle rather than shipping an empty cache.
- Spec scenarios for name resolution, idempotent re-run, exclusivity
  (`continue` unset), the rule→supervisor `agentId` binding, and both preflight
  warning paths are well covered (12 install specs).
- `dotnet build` clean under TreatWarningsAsErrors; full solution suite green
  (5930 tests).

## Assessment

N1 is a real correctness defect in a spec/design-mandated safety net: the
agent 409-conflict re-resolve builds a malformed URL, so the documented
concurrent-install race errors out with contradictory output instead of
skipping. It is also untested. It must be fixed before merge. N2 and N3 are
low-severity improvements a separate task can fold in.

<promise>FAIL</promise>
