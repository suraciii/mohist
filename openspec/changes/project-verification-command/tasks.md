# Implementation tasks

- [x] Add Project command persistence, DTO, grain validation, creation requirement, migration, snapshot, and raw test schema.
- [x] Add dedicated API route/body and actionable missing-configuration error.
- [x] Add CLI create/set/view output and help presentation coverage.
- [x] Add Web Project model/API/settings editor and query invalidation.
- [x] Add bind-time snapshot through coordinator, binding participant, and dispatch context.
- [x] Replace built-in six-lane verification with one generic task and generic recovery.
- [x] Generalize recovery source attribution and persisted-source self-retry fencing.
- [x] Remove lane semantics without rewriting persisted WorkflowRun state.
- [x] Add/update focused tests for Project config, binding, dispatch, recovery, profiles, CLI, Web, and migration.
- [ ] Update authoritative docs and run the full `npm run verify` gate; authoritative docs are updated and focused Server/CLI/Runner/Web checks plus `npm run test:fast` pass. `npm run verify` was attempted but requires a clean index and worktree.
