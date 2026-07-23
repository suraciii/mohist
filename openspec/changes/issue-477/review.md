# Review: Issue 477

## Findings

### [P1] Rewrite the persisted Run Profile ID during migration

`WorkflowProfileDataMigrator` reads a Run's selection from `WorkflowProfileIdKey` or only from `metadata.annotations.workflowProfileId` at `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowProfileDataMigrator.cs:320-329`, then always writes the renamed value back into that annotation path at lines 329-330. Current `WorkflowRun` persistence stores the public `WorkflowProfileId` at the JSON root, so an active Run with a backing key keeps the old ID at runtime after a reserved-ID rename. A terminal Run has a null backing key and a root Profile ID, so it is not migrated at all. Such Runs can later resolve a deleted/nonexistent old Profile, violating the requirement that every Run binding be rewritten and that terminal history retain the migrated public ID. Read both the current root field and supported legacy annotation shape, and rewrite the same persisted field that was found; cover active and terminal Runs with serialized `WorkflowRun` state.

### [P1] Convert malformed YAML into Definition validation diagnostics

`WorkflowProfileProvider.CreateOrUpdateAsync` calls `WorkflowProfileYamlParser.Parse` at `packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileProvider.cs:194` without handling parser syntax failures. `WorkflowProfileYamlParser.Parse` lets `YamlStream.Load` throw `YamlException` at `packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileYamlParser.cs:12-16`, while the create/edit routes only translate `WorkflowDefinitionValidationException`. Consequently a syntactically malformed Definition produces an unhandled 500 instead of the required rejected save with a clearly identified Definition-validation source. Normalize YAML parser exceptions into the authoritative Definition diagnostics (and add create/edit API coverage asserting a 4xx response and no persisted Profile).

<promise>FAIL</promise>
