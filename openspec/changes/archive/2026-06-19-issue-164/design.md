## Context

Attention-derivation logic — the four-rule decision that turns `Issue` records into the actionable items shown in the homepage "Needs attention" summary — currently lives inside the Kanban widget at `packages/web/src/widgets/kanban-board/model/homepage-attention.ts:21`. The widget is the only consumer, but it is the only consumer only because there is no other surface yet. Epic #9 (Dashboard) is the next surface that will need exactly the same derivation; if we wait, Dashboard will copy or re-derive the rules and Kanban + Dashboard will silently drift the first time a rule changes.

The derivation today is:
- Inputs: `Issue[]` plus the current `AgentStatus` (currently unused by the rules, but reserved for future use).
- Outputs: `AttentionItem[]` with the four evaluation-ordered rules — `Approval needed` (approval pending), `Integration failed` (Integrate stage + Blocked/Interrupted), `Interrupted`, `Needs action` (Blocked; uses `blockedReason` when present, otherwise title).
- Caller: `KanbanBoard` at `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx:471`.

The server-side authority for the underlying `Issue.health`, `Issue.workflowStage`, and `Issue.approvalState` fields remains `MohistDefaultWorkflowProjection.RuntimeStatus` (`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/MohistDefaultWorkflowProjection.cs`). The derivation is a pure read of those fields; the relocation MUST NOT change that read.

The `Issue` entity is already the central aggregate for issue-domain types and exports its public API from `packages/web/src/entities/issue/index.ts`. `AttentionItem` and the derivation belong there.

**Stakeholders**: Web UI (Kanban widget, future Dashboard Attention Hero), anyone running the existing kanban-attention tests (must stay green), server team (no change), Epic #9 author (gains a clean import surface).

## Goals / Non-Goals

**Goals:**
- Hoist `deriveAttentionItems` and `isIntegrateFailure` out of the widget and into `packages/web/src/entities/issue/model/attention.ts`.
- Re-export `AttentionItem`, `deriveAttentionItems`, and `isIntegrateFailure` from `packages/web/src/entities/issue/index.ts` so any UI surface can import them through the Issue public API.
- Update `KanbanBoard.tsx` to import from the new location and delete `packages/web/src/widgets/kanban-board/model/homepage-attention.ts`.
- Preserve observable behaviour exactly: same inputs → same outputs, item-for-item, in the same order.
- Land a unit test file at the new location (or migrate the existing widget-local one) so the rules are regression-locked at the canonical home.

**Non-Goals:**
- Implementing the Dashboard Attention Hero UI (that is Epic #9, this issue only prepares the seam).
- Adding or renaming attention categories.
- Changing the `AttentionItem` shape (fields, types, optionality).
- Touching `MohistDefaultWorkflowProjection.RuntimeStatus` or any server-side projection.
- Optimising the derivation (it stays O(n) over `issues`; n is the project issue count).

## Decisions

### 1. New module lives at `packages/web/src/entities/issue/model/attention.ts`

**Decision**: Place the hoisted derivation at `packages/web/src/entities/issue/model/attention.ts`, alongside the other Issue-domain model modules (`labels.ts`, `rebase-events.ts`, `timeline-events.ts`, `live-task.tsx`).

**Rationale**: All siblings are pure issue-domain logic. The derivation reads only `Issue`/`AgentStatus` and produces an issue-domain value type; it is a model concern, not a view concern. Putting it under `entities/issue/model/` keeps FSD-layer direction clean (entity → widget, not widget → widget).

**Alternatives considered**:
- `packages/web/src/shared/model/attention.ts` — would hide the bounded context (Issue); the rules are issue-specific and should be discoverable under the Issue entity.
- `packages/web/src/widgets/kanban-board/model/attention.ts` (new file, widget-local rename) — does not solve the domain-logic leak; Kanban and Dashboard would still import from a widget module, which violates FSD and creates a future circular-import risk.

### 2. Re-export through `entities/issue/index.ts`, not through a separate barrel

**Decision**: Add three lines to `packages/web/src/entities/issue/index.ts`:

```ts
export { deriveAttentionItems, isIntegrateFailure } from './model/attention'
export type { AttentionItem } from './model/attention'
```

**Rationale**: The existing entity barrel already mixes `api`, `lib`, and `model` exports; following the established pattern keeps discoverability symmetric with siblings like `rebase-events` and `timeline-events`. Consumers continue to import from `'../entities/issue'` and never reach across to a raw model path.

**Alternatives considered**:
- Have consumers import from `'../entities/issue/model/attention'` directly — bypasses the entity's public API, makes future refactors harder, and is inconsistent with every sibling in the directory.

### 3. `isIntegrateFailure` is exported, not inlined into `deriveAttentionItems`

**Decision**: Keep `isIntegrateFailure` as a named, exported predicate.

**Rationale**: The issue body explicitly names it as a hoisted artefact alongside `deriveAttentionItems`. Exporting it preserves the option to test or compose it independently (e.g. a future "is this single issue an integrate failure?" check on a Dashboard detail card) without re-deriving the rule. It is a one-line cost on the public API surface and matches the granularity the issue asks for.

**Alternatives considered**:
- Inline as a private helper inside `deriveAttentionItems` — slightly smaller public API, but loses the named-rule ergonomics the issue body calls out and prevents independent reuse.

### 4. Behaviour-preserving copy, no rule changes

**Decision**: The body of `deriveAttentionItems` and `isIntegrateFailure` is moved verbatim into the new module. Order of `if/else if` branches, the dedup `Set<string>`, the `_agentStatus` parameter name (leading underscore = intentionally unused), and the four label/detail strings all carry over unchanged.

**Rationale**: The issue's acceptance criterion #2 requires bit-for-bit parity and the existing tests as the regression net. The cheapest way to honour that is to copy the function bodies rather than re-style them.

**Alternatives considered**:
- Refactor the rule loop into a table-driven dispatch (e.g. an array of `{match, label}` predicates) — cleaner long-term, but introduces a non-trivial behaviour risk on a "behaviour unchanged" refactor and is out of scope per the Non-Goals.
- Make `agentStatus` non-positional (e.g. options object) — breaks the existing call site with no behaviour benefit; deferred until a rule actually reads `agentStatus`.

### 5. Tests move with the module

**Decision**: If a test file exists for the old `homepage-attention.ts`, rename it to `attention.test.ts` and place it next to the new module. If no test file exists, add a small one at `packages/web/src/entities/issue/model/attention.test.ts` that locks in the four-rule parity. Either way, the test imports come from the new module path.

**Rationale**: A refactor with no test at the canonical home is fragile — the next person to add a rule will have to find the test under the widget folder (or write a new one). Co-locating test and module matches the pattern of `labels.test.ts` next to `labels.ts` and `timeline-events.test.ts` next to `timeline-events.ts`.

**Alternatives considered**:
- Leave tests at the widget path importing from the entity — works, but the test name and location no longer reflect what is under test; future maintainers will be confused.
- Skip adding a test if none exists — acceptable, but a missed opportunity to lock in parity at the new home before any rule changes.

### 6. Single-commit, single-purpose change

**Decision**: Land the move as one commit touching (a) the new `attention.ts` (+ optional `.test.ts`), (b) the entity `index.ts` re-exports, (c) the `KanbanBoard.tsx` import, and (d) deletion of the old `homepage-attention.ts` (+ its test if migrated). No formatting churn, no incidental edits.

**Rationale**: A pure relocation is the easiest thing to review, revert, and bisect. Mixing in any other change (e.g. a future rule) would defeat the regression-net purpose of the existing tests.

## Risks / Trade-offs

- [Risk: Silent behaviour change during the move] -> Mitigation: copy function bodies verbatim; do not re-style; rely on the existing widget-local tests (and any new entity-local tests) as the regression net; review the diff for whitespace/import-only changes.
- [Risk: Cyclic import between `entities/issue` and `widgets/kanban-board`] -> Mitigation: the new module lives in the entity layer, the widget imports from it, and there is no reverse edge. The FSD direction entity → widget is already established by other entity modules consumed by widgets.
- [Risk: A future Dashboard implementer ignores the shared module and re-derives the rules] -> Mitigation: the proposal, spec, and this design all name the entity export as the canonical entry point; Dashboard's reviewer can flag a re-derivation. (Not a code-level guard — a process one.)
- [Risk: `isIntegrateFailure` becomes dead export until reused] -> Mitigation: acceptable; the issue explicitly asks to hoist it, and the cost of an unused export is negligible. Marking it `@internal` is rejected because the issue body calls for a clean public entry point for Dashboard.
- [Trade-off: Slightly larger public API of `entities/issue`] -> Justification: still bounded; three new symbols (one type, two functions) and they are all part of the bounded context the entity already owns.

## Migration Plan

This change is a pure refactor with no schema, API, or server-side impact, so there is no data migration and no rollback story beyond reverting the commit.

**Deployment steps:**
1. Land `packages/web/src/entities/issue/model/attention.ts` containing `deriveAttentionItems`, `isIntegrateFailure`, and `AttentionItem` (verbatim copy from the old file).
2. Land `packages/web/src/entities/issue/model/attention.test.ts` if no widget-local test exists, or migrate the existing one.
3. Update `packages/web/src/entities/issue/index.ts` with the three re-exports.
4. Update `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx:28` import to point at `'../../../entities/issue'`.
5. Delete `packages/web/src/widgets/kanban-board/model/homepage-attention.ts` (and its migrated test, if any).
6. Run the existing test suites (`vitest` for `packages/web`) — they MUST be green with no assertion edits.

**Rollback**: `git revert` the single commit. Behaviour is unchanged across the revert boundary.

**Feature flag**: None needed — the refactor has no observable effect and the existing UI continues to render identically.

## Open Questions

- Does any other widget or page in `packages/web/src` already import `deriveAttentionItems` from the widget-local path (beyond `KanbanBoard.tsx`)? A final `grep -r "homepage-attention"` sweep at implementation time will confirm. **Assumed**: no other importers based on the issue body and the directory listing.
- Should `AttentionItem` live in `entities/issue/model/types.ts` (with the other entity value types) or stay in its own `attention.ts` file? **Current decision**: stay in `attention.ts` so the four-rule module and its return type live together; can be moved to `types.ts` later if other entity code starts consuming `AttentionItem`. Trivial follow-up.
- Should the new public API mark `isIntegrateFailure` as the *only* exported predicate and keep `deriveAttentionItems` as the orchestrator, or should both be marked as the same stability tier? **Current decision**: both exported as stable; entity exports do not currently carry stability annotations and adding one here would be inconsistent.
