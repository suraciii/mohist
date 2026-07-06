# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking
  Scope: completeness
  Evidence: The plan (design.md D4, tasks.json acceptance criteria) claimed to identify "the most easily missed regression point" but only flagged ONE of TWO `Assert.Contains("install", stdout)` assertions that run `mo runner --help`. Verification against `packages/cli/tests/Mohist.Cli.Tests/CliRunnerCommandSpecs.cs` found a second such assertion at line 665 in `RunnerShow_HelpText_ListsShowAndExistingSubcommands` (the flagged one is at line 61 in `RunnerHelp_ListsListSubcommand`). After the convergence, `mo runner --help` no longer advertises `install`, so BOTH assertions must be flipped to `DoesNotContain`. The plan's acceptance criterion "no red test left behind" could not be met without also flipping line 665.
  Changed:
  - `design.md` D4 "现存正向 help 断言翻转" — now lists both assertions (行 61 + 行 665) instead of one.
  - `design.md` Risks section — updated to mention two assertions (行 61、行 665).
  - `tasks.json` T-001 description — now says "flip BOTH existing `mo runner --help` `Contains(\"install\")` assertions" with both test names and line numbers.
  - `tasks.json` T-001 acceptance criteria — the single-assertion criterion replaced with one requiring both assertions flipped.
  Verification: `rg -n 'Contains\("install"' packages/cli/tests/Mohist.Cli.Tests/` confirms exactly two hits (lines 61 and 665), both now referenced by the plan. No other `Contains("install"|"update")` assertions exist on `mo server --help` or `mo runner --help` (the remaining `Contains("update")` hits are in unrelated command contexts: project-workflow, agent, repository).
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: design.md D4 says "installer 是否被调用通过 fake（`Support/FakeServiceInstaller.cs` 已存在）观测". The existing `FakeServiceInstaller` is a pure no-op (returns 0 for every method) and does NOT record calls. For the "verb-root behavior unchanged" scenarios that must assert `mo install server` still invokes `IServiceInstaller.InstallServerAsync`, the implementer needs to either extend `FakeServiceInstaller` to record invocations or observe via exit code + output. This is trivial test-infrastructure work fully within T-001's scope ("Add regression guards") and does not block the plan's correctness — the parse-failure guards for deleted paths do not depend on recording (handler never runs on parse failure).
  SuggestedAction: During implementation, either add a call-recording list to `FakeServiceInstaller` or assert verb-root unchanged behavior via the `--dry-run` output / exit code anchors already used by neighboring specs.
  Status: follow-up

## Verification Summary

All plan claims were checked against the actual codebase (`packages/cli/Mohist.Cli/MohistCliCommands.Server.cs`, `MohistCliCommands.Install.cs`, `MohistCliCommands.Update.cs`, `packages/cli/tests/Mohist.Cli.Tests/`, `docs/cli-reference.md`):

- Code line numbers and structure match design claims: `ServerCommands.BuildInstall` (line 58), `ServerCommands.BuildUpdate` (line 80), `RunnerCommands.BuildInstall` (line 295), `installer`/`updater` locals (lines 12-13 in ServerCommands.Build, line 140 in RunnerCommands.Build), `Subcommands.Add` registrations (lines 16-17, 143).
- `mo runner update` confirmed never registered (`RunnerCommands.Build` only adds `install`) — D3 invariant is a real no-op-to-preserve.
- Dead-code boundary (D2) correct: `installer` retained (used by BuildSystemd + BuildLogs), `updater` deleted (only consumer is BuildUpdate).
- Verb-root entries (`InstallCommands`, `UpdateCommands`) correctly identified as untouched; same `IServiceInstaller` / `SourceCodeUpdater` methods, same flags.
- `docs/cli-reference.md` targets accurate: line 255 (Runner `install`), lines 271-272 (Server `install`/`update`), line 278 (migration note), line 340 (gap-table row); 「安装与升级（动词根集中）」 section (lines 320-332) is the canonical target and stays unchanged.
- `CliRootCommandShapeSpecs.cs` Legacy* pattern (lines 116-178) exists and is reusable as claimed.
- `CliReferenceDocsSpecs.cs` forbidden-legacy-row guard (lines 90-101) exists and is extensible as claimed.
- Alignment with issue #388 acceptance criteria: all 10 boxes trace to spec scenarios / task acceptance criteria; the `mo runner update` deletion criterion is correctly characterized as "naturally satisfied" (code never existed).
- Task granularity appropriate: single cohesive feature slice (delete 3 paths + guard + sync docs), no over-decomposition into "define interface"/"register DI"/standalone "add tests" tasks.
- Dependency completeness: single task, no `dependsOn` needed, no cycles.

<promise>PASS</promise>
