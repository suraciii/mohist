# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: docs/cli-reference.md
  Evidence: `docs/cli-reference.md:16` said all commands accept `--project` / `--project-id`, but the same candidate documents `mo workflow list` as not accepting project flags and live `mo workflow list --project demo` rejects them. Replaced the global claim with a scoped statement for project-scoped commands and a note that global commands follow their own `--help`.
  Verification: `mo workflow list --project demo` rejects `--project`; `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed 549/549; `git diff --check` passed.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: docs/cli-reference.md; docs/issues.md; packages/cli/tests/Mohist.Cli.Tests/CliReferenceDocsSpecs.cs
  Evidence: `mo issue rerun-from-stage` exists in live CLI help and is documented in the dispatcher skill, but the issue command cheat-sheet in `docs/cli-reference.md` and the "CLI 完整命令一览" in `docs/issues.md` omitted it. Added `mo issue rerun-from-stage <number> --stage <stage>` to both command lists, added a recovery table row in `docs/issues.md`, and extended the doc assertions.
  Verification: `mo issue --help` and `mo issue rerun-from-stage --help` show the command and required `--stage`; `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed 549/549; `git diff --check` passed.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: docs/cli-reference.md; packages/cli/tests/Mohist.Cli.Tests/CliReferenceDocsSpecs.cs
  Evidence: `docs/cli-reference.md` showed `mo otel query [<sql>]`, but the implementation returns an error when SQL is omitted (`mo otel query requires a SQL argument`). Changed the cheat-sheet to `mo otel query <sql>` and added a doc assertion for that form.
  Verification: `mo otel query --help` shows the SQL argument and the implementation requires non-empty SQL; `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed 549/549; `git diff --check` passed.
  Status: resolved

## Blocking Items

_(none)_

## Follow-up Items

_(none)_

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: verification scope
  Evidence: I ran the targeted CLI test project and command/cache checks for this docs/skill candidate, not the full repository test suite. The reviewed change is docs/skill oriented, with small CLI doc-test additions and no runtime product code changes.
  SuggestedAction: Run the broader suite (`npm test`) before integration if the workflow requires full-repo confidence beyond the CLI/docs scope.
  Status: out-of-scope

## Acceptance Evidence

- Epic skill: `packages/cli/Mohist.Cli/skill-data/mohist-create-epic/SKILL.md` documents `mo epic start`, `pause`, `resume`, idempotency, running-but-idle, and recommends autopilot over manual per-issue starts; stale "does not participate in workflow execution" / "non-executing organizer" wording is absent from product skill files.
- Dispatcher skill: `packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md` covers issue lifecycle commands including `reject`, `retry`, `rerun`, `rerun-from-stage`, `stop`, `force-stop`, `resume`, `rebase`, and epic autopilot `start` / `pause` / `resume`.
- User docs: `docs/epics.md` already contains Start/Pause/Resume, idempotency, running-but-idle, and typical workflow usage; `docs/cli-reference.md` documents `agent`, `label`, `workflow`, and `otel` groups and no longer claims Web UI equivalence or absolute complete-reference status.
- Boundary and operations-skill decision: `design/conventions.md` records the display-surface vs functional-entry boundary; `openspec/changes/issue-304/design.md` records the decision to keep operations in the dispatcher rather than creating `mohist-operate`.
- Skill cache: `diff -u packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md <(mo skills get mohist)` and `diff -u packages/cli/Mohist.Cli/skill-data/mohist-create-epic/SKILL.md <(mo skills get mohist-create-epic)` produced no output, so source and managed cache match byte-for-byte for the edited skills.
- CLI surface checks: `mo --help`, `mo agent --help`, `mo label --help`, `mo workflow --help`, `mo otel --help`, `mo issue --help`, and `mo issue rerun-from-stage --help` were checked against the documented command groups and repaired entries.

<promise>PASS</promise>
