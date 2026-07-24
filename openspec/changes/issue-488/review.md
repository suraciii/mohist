# Review — issue-488 (`mo agent install supervisor`)

Reviewer role: critical review of the change as it sits in the tree, against
`proposal.md`, `specs/agent-preset-install/spec.md`, `design.md`, `tasks.json`,
and the issue acceptance. No file other than this one was modified. Findings are
written so a separate fix task can act on each without re-investigation.

## How the change was verified

- Read all changed product sources (`MohistCliCommands.Agent.cs`,
  `PresetCatalog.cs`, `AgentInstallPreflight.cs`, `MohistCliApi.cs`), the preset
  resources (`presets/manifest.json`, `presets/supervisor/*.md`), the csproj
  Content entry, and the test files (`PresetCatalogTests.cs`,
  `CliAgentCommandSpecs.cs`).
- Traced the deployment path: how `mo update` lays the CLI out, how
  `SkillAssetRootResolver` resolves the skill-data root, and how `PresetCatalog`
  derives its own root from it.
- Ran `dotnet build packages/cli/Mohist.Cli/Mohist.Cli.csproj` (clean,
  TreatWarningsAsErrors satisfied) and `dotnet test packages/cli/tests/Mohist.Cli.Tests`
  filtered to Preset/AgentInstall (1365 passed).
- **Ran the built CLI against the real (non-overridden) `PresetCatalog` on this
  machine**, which is in the post-`mo update` steady state (`~/.mohist/cli/skill-data`
  present, `~/.mohist/cli/presets` absent):

  ```
  $ dotnet run --project packages/cli/Mohist.Cli/Mohist.Cli.csproj -- agent install supervisor
  Unknown preset 'supervisor'. Available presets: .
  ```

  Exit 1, no HTTP sent. The feature is non-functional in this state.

## Findings

### F1 — Feature is dead on the canonical deployment path (`mo update`) [Critical, must fix]

After `mo update`, the managed CLI lives at `~/.mohist/cli/mo`,
`~/.mohist/cli/skill-data/` is populated, and `~/.mohist/cli/presets/` **does
not exist**. Running `mo agent install supervisor` from that binary prints
`Unknown preset 'supervisor'. Available presets: .` and exits 1 before any HTTP
call. Every acceptance criterion of T-001/T-002/T-003 and the spec's "Known
preset name proceeds to installation" scenario are violated in this state —
which is the normal state on any machine that has run `mo update`.

Two layered defects cause it; both need fixing.

**Defect 1a — `mo update` never syncs `presets/` to the managed cache.**
`UpdateOperations.UpdateCliResolvedAsync` publishes to `.publish/cli/` (which
*does* contain `presets/` as a sibling of `skill-data/`, courtesy of the new
csproj Content entry), copies only the binary to the managed target, then syncs
only `skill-data`:

- `packages/cli/Mohist.Cli/Update/UpdateOperations.cs:149` —
  `var sourceSkillData = Path.Combine(publishDir, "skill-data");` (no equivalent
  for presets).
- `packages/cli/Mohist.Cli/Update/UpdateOperations.cs:229` —
  `await synchronizer.SyncAsync(sourceSkillData, managedSkillData);` (the one
  and only sync; `SkillAssetSynchronizer.SyncAsync` copies a single source dir
  to a single managed dir and validates `SKILL.md`, so it is not reusable as-is
  for presets either).

A grep of `packages/cli/Mohist.Cli/Update/` and `MohistCliCommands.Update*.cs`
for `presets` returns nothing. `mo update` is the documented install mechanism
(`AGENTS.md`: `mo update # 更新运行版本`), so this is the primary path, not an
edge case.

**Defect 1b — `PresetCatalog` derives its asset root from the skill-data root
by string substitution, rather than resolving independently.**
The default constructor runs the skill-data resolver and then substitutes the
last path segment:

- `packages/cli/Mohist.Cli/PresetCatalog.cs:21-22`:
  ```csharp
  _resolution = resolver.Resolve();
  _assetRoot = _resolution.AssetRoot is null ? null
      : Path.Combine(Path.GetDirectoryName(_resolution.AssetRoot) ?? _resolution.AssetRoot, "presets");
  ```

Because `SkillAssetRootResolver.Resolve()` checks **ManagedCache
(`~/.mohist/cli/skill-data`) before the Sibling fallback**, on a post-`mo update`
machine `_resolution.AssetRoot` is `~/.mohist/cli/skill-data`, so `_assetRoot`
becomes `~/.mohist/cli/presets`. That directory is missing (defect 1a) →
`ReadManifest()` returns null → `ListNames()` is empty → every name is unknown.

This also contradicts the design. D2 states presets resolve **independently**
("`AppContext.BaseDirectory/presets` 兜底，`MOHIST_SKILLS_DIR` 同源时不耦合——
预设独立解析") and names `AppContext.BaseDirectory/presets` as the fallback. The
implementation does the opposite: it couples to the skill-data root. Even if 1a
were fixed by syncing `presets/` next to `skill-data/`, the substitution would
still mis-resolve whenever `MOHIST_SKILLS_DIR` points at a custom location the
user did not intend for presets.

**Why tests did not catch it.** Every `PresetCatalogTests` and
`CliAgentCommandSpecs` case injects the root via the
`(IFileSystem, string assetRoot)` constructor or the `PresetCatalogOverride`
static hook (`CliAgentCommandSpecs.cs:27-29`). The default constructor's
derivation — the only path production code takes — has zero coverage. No test
exercises a managed-cache layout (skill-data present, presets sibling absent or
present).

Recommended fix (for the fix task):
1. Make `mo update` sync `presets/` alongside `skill-data/` to the managed cache
   (generalize `SkillAssetSynchronizer` or add a presets sync in
   `UpdateCliResolvedAsync`/`SyncSkillsAsync`).
2. Either resolve the preset root independently (own resolver / env var /
   `AppContext.BaseDirectory/presets` fallback, as D2 specifies) instead of
   deriving it from the skill-data root, or document the coupling as intentional
   and make both sync paths agree.
3. Add a spec/unit that exercises the **default** `PresetCatalog()` constructor
   against a fake managed-cache layout (skill-data present at
   `~/.mohist/cli/skill-data`, presets at the expected sibling) and one that
   proves `Resolve("supervisor")` succeeds post-update and fails clearly when
   presets are missing.

### F2 — Preflight skill-stub check probes the CLI's CWD, not the default repository's workspace [Medium, should fix]

The spec (`### Requirement: Check-only preflight warnings`) says the check
verifies whether "the default repository workspace exposes the `mohist` skill
stub (`.agents/skills/mohist`)". The implementation probes the CLI process's
current directory instead:

- `packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:107`:
  `preflight.Run(api.FileSystem.CurrentDirectory, defaultRepoResolved);`
- `packages/cli/Mohist.Cli/AgentInstallPreflight.cs:30`:
  `var skillStubPath = Path.Combine(workspacePath, ".agents", "skills", "mohist");`

`TryResolveDefaultRepositoryAsync` (`MohistCliCommands.Agent.cs:119-151`) fetches
the project, finds the `isDefault` repo, and returns only `(bool, repositoryName)`
— it discards any workspace-path information. The check therefore inspects
`<wherever the user ran mo>/.agents/skills/mohist`, which is unrelated to the
runner checkout. False positives when `mo` is invoked outside a workspace; false
negatives when it is invoked inside a tree that happens to have the stub but the
runner workspace does not. The warning is non-blocking, but it does not meet the
spec's "default repository workspace" framing.

The project endpoint does not appear to expose a per-repo workspace path (the
runtime `Workspace.Path` belongs to a `WorkflowRun`, not the repository), so a
fully correct check may be infeasible today. The fix task should at minimum make
`TryResolveDefaultRepositoryAsync` surface whatever location signal the repo
object does carry (e.g. local checkout path) and fall back to a documented
"unknown workspace, cannot check" notice rather than silently substituting CWD.

The existing tests pass only by coincidence: `FakeFileSystem.CurrentDirectory` is
hard-coded to `/repo` in `FileSystemWithProject()`, and the tests create/omit the
stub under `/repo/.agents/skills/mohist`. They are asserting against the wrong
path.

### F3 — No test asserts the rules' `agentId` resolves to the supervisor Agent [Low, should fix]

The spec's "Supervisor preset authoritative content" requirement fixes names,
match expressions, prompts, and the agent's instructions, but never normatively
states both rules' `agentId` SHALL resolve to the `supervisor` Agent (this is the
self-review's F1, left unaddressed). The implementation does bind correctly —
`EnsureRuleAsync` is passed `agent.Id` from `EnsureAgentAsync`
(`MohistCliCommands.Agent.cs:86,92,231`). But `AgentInstall_CreatesAgentAndRulesInOrder`
(`CliAgentCommandSpecs.cs:77-98`) only asserts `continue == null`; it never
asserts `agentId` equals the supervisor agent's id. A regression that bound a
rule to a wrong/stale/empty `agentId` would not be caught. Add an assertion on
the POSTed rule body's `agentId` (and ideally a spec scenario) per the
self-review's recommendation.

### F4 — `PresetCatalog` default constructor hard-wires `RealFileSystem`, forcing a process-global static override [Low, nit]

`PresetCatalog.cs:11-14`:

```csharp
public PresetCatalog()
    : this(RealFileSystem.Instance, SkillAssetRootResolver.CreateDefault(RealFileSystem.Instance, SystemEnvironmentVariableProvider.Instance))
```

Production code cannot inject a fake, so every test reaches in via
`AgentCommands.PresetCatalogOverride` — a `static Func<IFileSystem, PresetCatalog>`
that is process-global mutable state. This is the same anti-pattern that already
forced the `NotifyCommands.ConfigPathOverride` non-parallel collection
workaround in this very change (`CliAgentCommandSpecs.cs:14-17`,
`CliNotifySetupCommandSpecs.cs:9`). Threading a `PresetCatalog` (or a factory)
through `MohistCliApi` the same way `IFileSystem` already is would remove the
override entirely and make the default-constructor path (the one F1 hides in)
unit-testable.

## Positive notes

- Spec scenarios that *are* covered are covered well: idempotent re-run with
  edit preservation, partial pre-existence, unknown-name listing, tail ordering,
  exclusive (`continue` unset) rules, both preflight warning paths, the
  no-default-repo note, and the notification clean/missing cases. These all pass.
- The three shipped preset resources preserve `{{event.*}}` verbatim and carry
  the fixed names/match expressions mandated by the spec
  (`PresetCatalogTests.cs:11-27`).
- The agent-then-rules ordering correctly satisfies the rule-create `agentId`
  validation constraint the self-review called out.
- `dotnet build` is clean under TreatWarningsAsErrors; the CLI test suite is
  green (1365/1365 in the filtered run).

## Assessment

F1 is a hard blocker: the feature does not work on any machine in the
post-`mo update` steady state, demonstrated empirically above and traceable to
two concrete code locations. It must be fixed (and regression-tested against the
managed-cache layout) before merge. F2 is a spec-conformance gap that makes the
headline safety check misleading; F3/F4 are testability/assertion improvements.

<promise>FAIL</promise>
