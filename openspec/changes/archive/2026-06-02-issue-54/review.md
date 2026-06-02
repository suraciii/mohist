# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: dead code in `SkillAssetManifest.TryRead` (SkillAssetManifest.cs:120-122)
  Evidence: `SkillAssetManifest.TryRead` returns `Missing(...)` with a `manifest.json` is empty diagnostic when the deserialized document is `null`. The surrounding code never produces a `null` `SkillAssetManifestDocument` from a deserialization of `{}` (System.Text.Json returns an empty object instance), and the next branch already handles schema validation. The branch is unreachable in practice; leaving it in is harmless but it is dead defensive code introduced by this change.
  Verification: Inspected all `JsonSerializer.Deserialize<SkillAssetManifestDocument>` call sites; under .NET 10 a non-null JSON object deserializes to a non-null document, and the only path that could yield `null` is a JSON literal `null` which would not be a valid manifest in real use.
  Status: not-repaired (kept as defensive code; no behavior change required)

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: cross-cutting docs
  Evidence: `docs/README.md` documents `mo skills` behavior at lines 121-166 but does not mention the new `~/.mohist/cli/skill-data` managed cache or how a stale/incompatible cache is repaired. Operators reading the README will not know that `mo skills get <name>` is now backed by a managed asset cache and how repair works.
  SuggestedAction: Add a short paragraph under the existing `### Coder Agent Skills (mo skills)` section explaining that packaged content is served from `~/.mohist/cli/skill-data` after `mo update` / `scripts/install-mo.sh`, and that mismatches should be repaired by re-running those commands.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: pre-existing inconsistency
  Evidence: `SourceCodeUpdater.UpdateCliAsync` (MohistCliCommands.Update.cs:192) publishes to `Path.Combine(root, ".publish", "cli")` while `scripts/install-mo.sh` (line 14) publishes to `$REPO_ROOT/.publish/mo`. Both install/update paths work because each publishes into its own expected subdirectory, but the divergence is pre-existing and not introduced by this change.
  SuggestedAction: Pick one canonical publish subdirectory (e.g. `.publish/cli`) and align both `UpdateCliAsync` and `install-mo.sh`. Out of scope for issue-54 but worth tracking.
  Status: follow-up (pre-existing)

- [ID: item-3]
  Severity: follow-up
  Scope: SkillAssetSynchronizer diagnostics
  Evidence: `SkillAssetSynchronizer.SyncAsync` error messages (SkillAssetSynchronizer.cs:18, 24, 30, 38, 60, 71) consistently end with `"Aborting managed asset sync."` and do not include the `'mo update' or 'scripts/install-mo.sh'` repair hint that the issue spec requires. The synchronizer is only invoked from `SourceCodeUpdater.UpdateCliAsync` (which emits its own abort message), so users running `mo skills get <name>` see the richer `SkillAssetService` diagnostic instead. The internal synchronizer messages are operator-visible only when `mo update` itself fails mid-sync, where the surrounding CLI already provides the abort context.
  SuggestedAction: Optional polish: append `"Repair by running 'mo update' or 'scripts/install-mo.sh'."` to the synchronizer's error lines so the hint is consistently surfaced regardless of the entry point.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: pre-existing
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/RunnerBindingSpecs.Poll_WhenRegistryEntryMissing_ReRegistersRunnerPresence`
  Evidence: This test fails on the current change base with `Assert.Contains() Failure: Item not found in collection` at RunnerBindingSpecs.cs:149. `git log` shows the spec file was last modified by `faadeb040b Simplify RunnerGrain` which predates the issue-54 work; no file under `packages/cli/`, `packages/server/src/`, `packages/server/tests/`, or `scripts/` relevant to RunnerBinding is changed in this PR (`git diff aee4585ec1..HEAD -- RunnerBindingSpecs.cs RunnerGrain.cs` returns empty).
  SuggestedAction: Unrelated to issue-54. Track separately as a pre-existing test flake/bug.
  Status: pre-existing (does not block this change)

- [ID: item-2]
  Severity: out-of-scope
  Scope: `SourceCodeUpdater.UpdateCliAsync` publish path
  Evidence: As described in follow-up item-2, the `.publish/cli` vs `.publish/mo` divergence is pre-existing and outside the scope of issue-54 (which only adds asset synchronization; it does not modify publish output paths).
  SuggestedAction: Track as a separate cleanup issue.
  Status: out-of-scope

## Spec Compliance Verification

Each acceptance criterion from the issue was verified against the changed code and tests.

| Acceptance Criterion | Status | Evidence |
|----------------------|--------|----------|
| `mo skills get mohist` works after `mo update` without `MOHIST_SKILLS_DIR` | PASS | Verified end-to-end by running `bash scripts/install-mo.sh` against a temp `HOME` and then `mo skills get mohist` (output: full `mohist` skill guidance, no `MOHIST_SKILLS_DIR` set). `UpdateInstallSyncSpecs.UpdateCliAsync_EnablesSkillAssetServiceResolution_WithoutMohistSkillsDirOverride` covers the same path. |
| `mo skills get mohist-explore` works after `mo update` without `MOHIST_SKILLS_DIR` | PASS | Same end-to-end test exercises the explore path. `SkillsContentSpecs.Get_ReturnsFullPackagedGuidance_FromManagedCache_WhenSelected` covers the resolver path. |
| `scripts/install-mo.sh` installs both the `mo` binary and packaged skill assets | PASS | `UpdateInstallSyncSpecs.InstallScript_InstallsBinaryAndSynchronizesSkillData_WithoutTouchingExternalSkillDirectories` (passes in 7s; calls the actual `bash scripts/install-mo.sh`); script changes at `scripts/install-mo.sh:62-119`. |
| `mo skills path mohist` reports the managed asset path when the managed `~/.mohist/cli/skill-data` copy is in use | PASS | `SkillsContentSpecs.Path_PrintsManagedCachePath_WhenManagedCacheIsSelected` asserts the path equals `Path.Combine(managedRoot, "mohist")`; `SkillsCommandBehaviorSpecs.PublishedCommands_ResolveFromManagedCache_WhenManagedCacheIsPresent` (line 96-103) exercises the same behavior through the published CLI. End-to-end test confirmed. |
| `MOHIST_SKILLS_DIR` remains supported and takes precedence | PASS | `SkillAssetRootResolverSpecs.Resolve_PrefersOverrideDirectory_OverManagedCacheAndSiblingFallback` and `Resolve_PrefersOverrideDirectory_OverSiblingFallback_WhenManagedCacheIsAbsent` cover the precedence; `SkillsContentSpecs.Commands_UseMohistSkillsDirOverride_ForListGetAndPath` exercises the env variable. |
| Mismatched/missing/incompatible managed assets fail with repair guidance explaining `mo update` / `scripts/install-mo.sh` | PASS | `SkillAssetRootResolverSpecs.Resolve_ReportsManagedManifestMissing_WithRepairGuidance`, `Resolve_ReportsManagedVersionMismatch_WithoutFallingBackToSibling`, `Resolve_ReportsManagedGitHashMismatch_WithRepairGuidance`, `Resolve_ReportsOmittedBuiltInSkill_WithRepairGuidance`, `Resolve_ReportsMissingSkillMarkdownFile_WithRepairGuidance`, `Resolve_ReportsMalformedManagedManifestJson_WithRepairGuidance` all assert the diagnostic contains `mo update` and `scripts/install-mo.sh`. `SkillsContentSpecs.Get_FailsWithRepairGuidance_WhenManagedCacheIsIncompatible` verifies the diagnostic reaches the command-level error. |
| Implementation does not read, write, or mutate runtime/internal `.mohist/skills` | PASS | `SkillAssetRootResolverSpecs.Resolve_DoesNotReadWriteOrMutateRuntimeDotMohistSkills` and `Resolve_DoesNotReadWriteOrMutateRuntimeDotMohistSkills_WhenResolutionFails` snapshot the runtime skills directory before and after, asserting equality; `UpdateInstallSyncSpecs.UpdateCliAsync_DoesNotModifyRuntimeMohistSkillsDirectory` asserts the same for `mo update`; `UpdateInstallSyncSpecs.InstallScript_InstallsBinaryAndSynchronizesSkillData_WithoutTouchingExternalSkillDirectories` (lines 257-273) sets sentinel files in `${HOME}/.mohist/skills` and verifies they remain untouched after the install script. |
| Tests cover: default managed asset root, env override precedence, update/install sync, missing asset diagnostics, sibling fallback | PASS | All listed scenarios have dedicated tests (see Spec Compliance table above and Review Dimensions below). |

## Review Dimensions

- **Correctness**: Logic for resolver precedence (`MOHIST_SKILLS_DIR` → managed cache → sibling fallback) is correct; the synchronizer uses a temp-dir + rename pattern that is "atomic enough" per design; managed cache path resolution uses the user-profile-derived `~/.mohist/cli/skill-data` consistently in C# code, the install script, and tests. Manifest generation reads `AssemblyInformationalVersionAttribute` first, falls back to `MOHIST_GIT_HASH` and `git HEAD`, which gives stable identity in dev/publish.

- **Complexity**: The resolver, manifest, and synchronizer are each small, single-purpose, and well-isolated. `SkillAssetService` retains its prior surface and only adds diagnostics; `SourceCodeUpdater` only adds 25 lines for sync. No unnecessary coupling introduced.

- **Test Coverage**: 76 skill-related tests pass (SkillsCli* 76 specs, plus the touched UpdateSpecs). Tests cover: manifest round-trip, normalization, validation, all mismatch paths, resolution precedence (override, managed, sibling, none), mutation safety of `.mohist/skills`, end-to-end `mo update` and `scripts/install-mo.sh` execution, and command-level behavior from the published binary. The full `dotnet test` run is 495 passed, 1 pre-existing unrelated failure, 3 skipped.

- **Security**: No new attack surface. The synchronizer writes only inside the `~/.mohist/cli` parent directory and only when the temp directory and source validation succeed. Manifest content is JSON; the validator rejects malformed input with repair guidance. No secrets are written, no executable permissions are changed, and no path-traversal target outside `~/.mohist/cli` is reachable from the sync flow. Install script restricts itself to `~/.local/bin/mo` and `~/.mohist/cli/skill-data`. `SkillAssetRootResolver` only resolves from environment, user profile, and the executable's base directory, with no arbitrary input.

- **Spec Compliance**: All acceptance criteria verified (table above). The change does not add `mo skills update`, does not write user-authored or external agent skills into the managed asset directory, does not require a running Mohist server, and treats `~/.mohist/cli/skill-data` as a CLI-owned cache (not editable user configuration).

## Test Run Summary

```
Filter FullyQualifiedName~Skill -> 76/76 passed
Filter FullyQualifiedName~UpdateSpecs -> 22/22 passed
Filter FullyQualifiedName~InstallScript -> 1/1 passed
Full Mohist.Server.Tests -> 495 passed, 1 failed (pre-existing RunnerBindingSpecs.Poll_WhenRegistryEntryMissing_ReRegistersRunnerPresence, unrelated), 3 skipped
```

The single failure was confirmed pre-existing (last RunnerBindingSpecs.cs change predates issue-54; no related file was touched in this change).

<promise>PASS</promise>
