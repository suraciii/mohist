## Findings

### P1: Preserve all positive legacy Issue numbers accepted before the migration

`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260728000000_TypedWorkflowRunLineage.cs:100` now requires the legacy string to equal `CAST(CAST(value AS INTEGER) AS TEXT)`. That rejects `"+5"`, `"042"`, and whitespace-padded positive numbers, even though the preceding implementation used `int.TryParse` for lineage and accepted those values. The new test at `packages/server/tests/Mohist.Server.SpecTests/Specs/Workflow/Storage/TypedWorkflowRunLineageMigrationSpecs.cs:73` incorrectly classifies `"042"` and `"+5"` as malformed. On upgrade, these previously usable historical Runs retain only their annotations, which the new code no longer reads, so ownership and event lineage fail after reload. Validate without prefix truncation, but preserve the old positive-integer acceptance set (or explicitly transform it to the canonical typed integer), and distinguish those cases from suffix, exponent, and decimal inputs in the migration specs.

<promise>FAIL</promise>
