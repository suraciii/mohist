## Why

Attention-derivation logic currently lives inside the kanban widget (`packages/web/src/widgets/kanban-board/model/homepage-attention.ts`), so any other surface that needs to surface "this issue needs attention" — starting with the Dashboard Attention Hero (Epic #9) — would have to copy or re-derive it. That is a domain-logic leak: when an attention rule changes (e.g. a new category), Kanban and Dashboard will drift and disagree silently. Hoisting the logic into the shared Issue context now gives every consumer a single source of truth while the surface area is still small.

## What Changes

- Move `deriveAttentionItems` and `isIntegrateFailure` out of `packages/web/src/widgets/kanban-board/model/homepage-attention.ts` into a shared Issue-context module (e.g. `packages/web/src/entities/issue/model/attention.ts`).
- Re-export `AttentionItem` (and the derivation entry point) from the new shared location so non-widget consumers (future Dashboard) can import it without crossing into a widget module.
- Update the kanban widget to import the derivation from the new shared location; delete the original file.
- Preserve the existing kanban behaviour bit-for-bit: same `Issue[] + AgentStatus` input, same `AttentionItem[]` output, same rule semantics (approval-pending / integrate-failure / interrupted / blocked) and same interaction with server-side `MohistDefaultWorkflowProjection.RuntimeStatus`.
- Keep tests green — existing `homepage-attention` coverage (and any kanban-board tests that exercise the widget's attention list) must continue to pass unchanged once pointed at the new module.
- Expose the derivation through the shared Issue public API in a way that future Dashboard (Epic #9, non-goal here) can consume directly.

## Capabilities

### New Capabilities

- `issue-attention-derivation`: Shared domain logic that, given a list of `Issue` (and the current `AgentStatus`), derives the set of `AttentionItem` values that surface a "this issue needs user attention" signal. Covers approval-pending, integrate-failure, interrupted, and blocked rules; consumed by any UI that needs to render an attention list.

### Modified Capabilities

- `web-ui`: The kanban widget's attention list now reads from the shared Issue-context derivation module instead of its own local model file. Visible behaviour is unchanged; only the import source moves. (No new spec required beyond the new capability above, but listed for traceability because the widget itself changes.)

## Impact

- **File move**: `packages/web/src/widgets/kanban-board/model/homepage-attention.ts` → new shared location under `packages/web/src/entities/issue/model/` (likely `attention.ts`).
- **Public API of `entities/issue`**: gains an `AttentionItem` type and a `deriveAttentionItems` export — additive change, no breakage for existing entity consumers.
- **Kanban widget**: import path updates; widget behaviour and tests unchanged.
- **Dashboard (Epic #9, future)**: gains a single, documented import path for the same logic — no behaviour change in this issue.
- **Server side**: untouched. `MohistDefaultWorkflowProjection.RuntimeStatus` semantics remain the authority the derivation depends on.
- **Risk surface**: low. Pure relocation refactor with existing tests as the regression net.
