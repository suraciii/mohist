# Review — issue-488 (`mo agent install supervisor`) — final pass

Reviewer role: independent review of the change as it sits in the tree now,
against `proposal.md`, `specs/agent-preset-install/spec.md`, `design.md`,
`tasks.json`, and the issue acceptance. No file other than this one was
modified. This is the third pass; it judges the current state after the
F1–F4 and N1–N3 fix rounds landed.

## How the change was verified

- Read the spec and the changed product sources (`PresetCatalog.cs`,
  `PresetAssetRootResolver.cs`, `ManagedAssetSynchronizer.cs`,
  `MohistCliCommands.Agent.cs`, `AgentInstallPreflight.cs`,
  `Update/UpdateOperations.cs`, `Mohist.Cli.csproj`) and the preset resources.
- `dotnet build Mohist.sln` clean (TreatWarningsAsErrors satisfied).
- `dotnet test Mohist.sln` green: Workflow.Definition 175, Cli 1374,
  Server.Unit 1346, Arch 32, Spec 3006.
- Empirically ran the built CLI in a simulated post-`mo update` managed-cache
  state (`~/.mohist/cli/presets` populated, no sibling dir): `mo agent install
  acme` prints `Unknown preset 'acme'. Available presets: supervisor.` — the
  catalog resolves `supervisor` from the managed cache (the prior headline
  failure is gone).

## Acceptance-criteria coverage (all met)

- **Preset name resolution** — `install <preset>`, catalog ships `supervisor`
  only, unknown rejected with available names. Specs: `UnknownPresetLists…`,
  `WhenManagedPresetsAbsent_ExitsNonZeroBeforeAnyHttp`.
- **Authoritative content** — three resources, fixed names/match expressions,
  `{{event.*}}` preserved verbatim; agent created with no AgentConfig/Skills/
  MaxConcurrentRuns. Specs: `CreatesAgentAndRulesInOrder`,
  `ResponsePromptPlaceholdersFlowThroughToRuleBodyVerbatim`.
- **Idempotent installation** — list-then-create, skip-if-exists, 409 safety
  net, no overwrite of existing resources. Specs: `RerunSkipsExisting…`,
  `PartialPreexistence_CreatesOnlyTheMissingRule`,
  `AgentCreateConflict_ResolvesExistingAndBindsRulesToIt`.
- **Tail append / exclusive** — rule POST bodies carry no `before`/`after`
  anchor and `continue` is null. Pinned in `CreatesAgentAndRulesInOrder`.
- **Check-only preflight** — skill-stub + notification checks, non-blocking,
  named remediation. Eight specs cover every warning path, the clean case, the
  no-default-repo note, and project-fetch failure.

## Prior-round findings — resolution status

All findings from the two prior rounds are fixed and stay fixed:

- **F1 (critical deployment break)** — `mo update` syncs `presets/` alongside
  `skill-data/` (`UpdateOperations.cs:150-152,164,239-244`); `PresetCatalog`
  resolves its root independently via `PresetAssetRootResolver` (managed cache
  → sibling), no longer derived from the skill-data root. Verified empirically.
- **F2 (preflight CWD-vs-workspace framing)** — honestly reframed as a
  CWD-as-proxy check that names the resolved default repo
  (`AgentInstallPreflight.cs:17-48`).
- **F3 (rules' agentId binding untested)** — asserted in
  `CreatesAgentAndRulesInOrder`.
- **F4 (static PresetCatalogOverride)** — removed; catalog built from
  `api.FileSystem` + `api.GetUserHome`.
- **N1 (agent 409 re-resolve malformed URL)** — `EnsureAgentAsync` now receives
  the real `resolution.ProjectId` and passes it to `ResolveAgentAsync`
  (`MohistCliCommands.Agent.cs:74,141,180`); the URL-slicing trick is gone.
  The regression spec was confirmed to fail against the reverted bug.
- **N2 (validation error hardcoded by label)** — `ManagedAssetKind` now carries
  `InvalidPreparedMessage`; `TryValidatePrepared` reads it from the descriptor.
- **N3 (three scenarios pinned)** — tail-anchor absence, verbatim placeholders
  at the HTTP boundary, and partial pre-existence are all pinned by dedicated
  specs.

## Informational items (not must-fix)

These are documented open questions / pre-existing patterns, not defects in
this change, and do not block merge:

- **Archived-supervisor edge case** (self-review open question). `GET
  /agents?all=true` includes archived agents. If a user previously *archived*
  an agent named `supervisor`, install would detect "exists" and skip agent
  creation, then the rule POST would reject with `agent_archived`
  (`RoutingRuleStore.cs:161`). Narrow, not covered by the spec, degrades to a
  clear server error. Reasonable follow-up: treat archived-same-name as
  "does not exist for install" (filter the existence list to non-archived).
- **`NotifyCommands.ConfigPathOverride` static.** The install preflight
  (`MohistCliCommands.Agent.cs:104`) is a new consumer of this pre-existing
  process-global static. The cross-test hazard is already mitigated by the
  non-parallel `NotifyCommandConfigPath` collection (landed in the flaky-spec
  fix). Threading the config path through `MohistCliApi` would remove the
  static entirely but is a separate, larger refactor.

## Assessment

Every acceptance criterion is met, every prior review finding is resolved and
verified, the deployment path works empirically, and the full solution suite
is green with a clean build. No problems that must be fixed before merge.

<promise>PASS</promise>
