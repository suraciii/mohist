# Review Findings

## 1. `mo info` still exposes a boolean JSON mode

**Severity: blocking**

`packages/cli/Mohist.Cli/InfoCommands.cs:16` registers `Option<bool>("--json")` for a command that returns a structured system-information resource. It does not accept comma-separated fields, does not provide bare-`--json` field discovery, and directly chooses a complete JSON rendering. This leaves a resource-returning leaf outside the required shared `--json <fields>` contract and allows the same flag to have incompatible semantics across command families.

## 2. The required CLI test suite is still failing

**Severity: blocking**

`dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` currently reports `131` failures out of `1,399`. Remaining failures include tests that still assert envelope/raw JSON behavior instead of selected fields, old YAML/output-mode expectations, legacy project-option help/rejection expectations, and resource-specific field selections that currently request only `id` while asserting other fields. The task acceptance explicitly requires the CLI test suite to pass; the repository therefore still has no passing verification for the complete migrated command surface.

<promise>FAIL</promise>
