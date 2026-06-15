## Context

The current `integrate:spec-sync` task uses `mohist/openspec-sync`, a script action (`openspecSyncAction` in `packages/runner/src/actions/openspec.ts:39-48`) that copies the entire `specs/` directory from the change folder to `{workspace}/specs/`. This violates the OpenSpec delta-merge protocol: spec changes carry ADDED/MODIFIED/REMOVED/RENAMED intent that must be intelligently merged into the canonical `openspec/specs/` source of truth.

The existing specs in `openspec/specs/` (workflow-definition REQ-WD-001, workflow-engine REQ-WFE-005/006, change-artifacts REQ-CA-003) already define the required intelligent sync behavior — the implementation simply doesn't match the spec. This issue closes that gap by moving spec-sync from a file-copy script action to an agent-driven merge task.

## Goals / Non-Goals

**Goals:**
- Replace `mohist/openspec-sync` with `mohist/acp-agent` for `integrate:spec-sync`
- The agent reads delta specs and existing main specs, parses delta sections, merges with classification correction, validates, and writes to `openspec/specs/`
- Agent produces structured merge output (added, modified, removed, renamed per capability, plus corrections)
- `{workspace}/specs/` is no longer created
- Failure semantics (archive and merge blocked) are preserved

**Non-Goals:**
- Changing archive-change or merge behavior (those work correctly)
- Programmatic sync logic — the agent handles merge via prompt instructions
- Removing `openspecSyncAction` from the registry (keep for backward compat)
- Handling parallel changes to the same spec (future work)
- Modifying any existing spec requirements (they already describe the correct behavior)

## Decisions

### Decision 1: Agent-driven sync over programmatic sync

**Choice**: Use `mohist/acp-agent` with a structured prompt instead of writing a new programmatic action handler.

**Rationale**:
- The agent understands spec file structure, requirement names, and section headers through natural language — no parser code needed
- Edge cases (slight naming variations, formatting quirks) are handled by LLM judgment rather than hard-coded rules
- Classification mistakes (MODIFIED that should be ADDED) require semantic understanding of whether a requirement is "new" or "modifying existing" — perfect for an LLM
- Merge reports benefit from natural language summarization
- Follows the same pattern as other plan-stage artifact generation tasks (proposal, specs, design, tasks, self-review), keeping the architecture consistent

**Alternatives considered**:
- *Programmatic merge logic*: More deterministic but would require a full OpenSpec parser, merge resolver, and validator. Higher implementation cost, less flexible for edge cases. Not justified for a task that runs once per issue completion.
- *Keep current file-copy but fix the destination*: Still violates delta-merge protocol. Would silently drop deltas.

### Decision 2: Prompt as a new builtin `.prompt` file

**Choice**: Create a new `spec-sync.prompt` in `packages/server/src/Mohist.Server/Workflow/Services/Prompts/builtins/` using the existing YAML-frontmatter + artifact schema format.

**Rationale**:
- Follows the exact pattern of `proposal.prompt`, `specs.prompt`, `design.prompt`, etc.
- Loaded by `FilePromptLoader`, surfaced as `${{ prompts.spec-sync }}` in workflow YAML
- Supports project-level overrides through `ProjectWorkflowProfileManager` (same as other prompts)
- Keeps prompt content version-controlled alongside code

### Decision 3: Workflow YAML change — swap uses, keep id and title

**Choice**: Change only the `uses` and `with` fields of the `integrate:spec-sync` task in `mohist-default.workflow.yaml`:

```yaml
- id: integrate:spec-sync
  title: Sync specs
  uses: mohist/acp-agent
  with:
    session: integrate
    prompt: ${{ prompts.spec-sync }}
    agent: ${{ vars.agent }}
```

**Rationale**:
- Task `id` and `title` remain unchanged — WorkflowRun task identity is preserved
- `session: integrate` ensures the agent session is named for the integrate stage
- `agent: ${{ vars.agent }}` uses the standard agent config variable
- No changes to task ordering, failure propagation, or check dependencies

### Decision 4: Keep openspecSyncAction registered, unused for integrate

**Choice**: Leave `openspecSyncAction` registered in the action registry but remove its only caller.

**Rationale**:
- Removing the registration could break tests or other potential callers
- The function is small and harmless to keep
- Can be deprecated/removed in a follow-up cleanup issue
- No risk of accidental use since the only workflow YAML task that references it is being changed

### Decision 5: No stage-specific session re-use required

**Choice**: The spec-sync agent runs in a fresh `integrate` session, not reusing any prior stage session.

**Rationale**:
- Spec-sync runs once per issue, late in the pipeline (Integrate stage)
- No benefit to sharing session state with earlier Plan/Build/Check stages
- Simpler lifecycle — agent starts, reads files, merges, writes, reports, exits
- Avoids context pollution from earlier stages

## Risks / Trade-offs

- **[Risk] Agent hallucination produces malformed specs** → The prompt includes strict output format requirements and validation instructions. If the agent writes malformed content, REQ-SSA-007 validation catches it (duplicate headers, missing scenarios, residual delta headers) and the task fails before archive/merge proceed. Specs are never silently corrupted.

- **[Risk] Agent may miss or incorrectly handle complex rename chains** → The prompt explicitly instructs the agent to fail on ambiguous or destructive deltas (REMOVED with unknown source, RENAMED FROM with unknown source). This matches REQ-SSA-004 scenarios. The failure output includes structured conflict details for human resolution.

- **[Trade-off] Less deterministic than programmatic logic** → Agent behavior varies slightly between runs. However, the task runs once per issue completion, with auditable output. If the agent produces incorrect results, the output reveals what happened and the issue can be retried. Specs are also version-controlled, so git history provides a safety net.

- **[Trade-off] Agent latency** → Agent-driven tasks take longer than file copies (seconds vs. minutes). This is acceptable because Integrate runs once per issue, not in a hot loop, and correctness matters more than speed.

## Migration Plan

1. Create `spec-sync.prompt` in `builtins/` — no existing behavior changes
2. Update `mohist-default.workflow.yaml` `integrate:spec-sync` to use `mohist/acp-agent` with new prompt
3. No database migrations, API changes, or runner infrastructure changes needed
4. **Rollback**: Revert the workflow YAML change and re-point to `mohist/openspec-sync` (the handler still exists in registry). No data migration to undo.

## Open Questions

- **Should `openspecSyncAction` be explicitly deprecated or left as-is?** → Leave as-is for now; remove in a cleanup pass once the agent-driven path is proven stable.
- **Should the agent commit its changes?** → No. The agent writes to `openspec/specs/` only. The existing `integrate:merge` step handles the final squash-merge commit. Spec sync changes are included in that merge.
