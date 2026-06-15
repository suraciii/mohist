## Why

The current `integrate:spec-sync` task uses `mohist/openspec-sync` which blindly copies the entire `specs/` directory from the change folder to a workspace-local directory. This violates the OpenSpec delta-merge protocol where spec changes carry ADDED/MODIFIED/REMOVED/RENAMED intent that must be intelligently merged into the canonical `openspec/specs/` source of truth. Users expect completed issue specs to land in the project's main specs, not a transient workspace directory.

## What Changes

- Replace `mohist/openspec-sync` with `mohist/acp-agent` for `integrate:spec-sync` **BREAKING**: the agent must produce merge results as output, not a copied directory
- The agent reads delta specs from `openspec/changes/issue-{number}/specs/` and existing main specs from `openspec/specs/`
- The agent parses ADDED, MODIFIED, REMOVED, and RENAMED sections from delta specs
- The agent intelligently merges deltas into main specs, resolving obvious classification mistakes (e.g., MODIFIED with no matching source requirement becomes ADDED)
- The agent writes merged specs to `openspec/specs/` (not `{workspace}/specs/`)
- The agent reports merge results: what was added, modified, removed, or renamed
- Post-merge validation ensures no malformed specs land in main specs
- `{workspace}/specs/` directory is no longer created by spec-sync
- If spec-sync fails, `integrate:archive-change` and `integrate:merge` do not proceed (existing spec contract preserved)

## Capabilities

### New Capabilities

- `spec-sync-agent`: Agent-driven intelligent OpenSpec spec sync. The agent reads change delta specs and existing main specs, parses ADDED/MODIFIED/REMOVED/RENAMED delta intent, intelligently merges and resolves classification mistakes, validates results, writes merged specs to `openspec/specs/`, and reports merge outcomes as auditable transient output.

### Modified Capabilities

<!-- No existing spec requirements change. The requirements in workflow-definition (REQ-WD-001), workflow-engine (REQ-WFE-005, REQ-WFE-006), and change-artifacts (REQ-CA-003) already describe the intelligent sync behavior. This issue implements those requirements via an agent-driven mechanism instead of the current file-copy script action. -->

## Impact

- **Workflow definition** (`mohist-default.workflow.yaml`): `integrate:spec-sync` task changes from `uses: mohist/openspec-sync` to `uses: mohist/acp-agent` with a spec-sync prompt
- **Runner actions** (`packages/runner/src/actions/openspec.ts`): `openspecSyncAction` is no longer used for integrate:spec-sync; may remain for other callers or be deprecated
- **Agent prompt**: New prompt template instructs the agent on delta parsing, main spec reading, merge logic, correction rules, validation, and output format
- **Integration task ordering**: Existing ordering (`spec-sync` → `archive-change` → `merge` → `health`) is preserved; spec-sync failure semantics remain unchanged
- **Output destination**: Sync output now targets `openspec/specs/` instead of `{workspace}/specs/`
