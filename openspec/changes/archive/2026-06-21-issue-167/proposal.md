## Why

When a user glances back at Mohist, their first question is *"is there anything that needs me right now?"* Today the Dashboard's `Attention` slot is still an empty placeholder — issue #163 left the mount point, and #164 already promoted the attention judgment to a shared `deriveAttentionItems` in the Issue context. Without a Hero, the user is forced to scan the Kanban board to discover awaiting approvals, blocks, interruptions, or a downed runner. This issue closes that gap by turning the shared derivation into a top-of-dashboard, action-first surface so the answer is visible at a glance and reachable in one click.

## What Changes

- Add an **Attention Hero** view that mounts into the Dashboard `Attention` slot left by issue #163.
- **Two-state, self-adaptive rendering** driven solely by `deriveAttentionItems(issues, agentStatus)` + `useAgentStatus()`:
  - **Has-attention state**: lists each `AttentionItem` (Approval needed / Integration failed / Interrupted / Needs action) and, when `agentStatus.runnerAvailable === false`, a Runner-down entry. Each row exposes a direct action — jump to issue detail, Resume (`POST /issues/{n}/resume`-class), or Approve (approval endpoint).
  - **All-clear state**: when there are no attention items and the runner is available, show an `All clear` message with a placeholder pointer to the Productivity preview (real content arrives with issue G).
- The Hero is **pure consumption UI**: it imports `deriveAttentionItems` and `AttentionItem` from the shared Issue context (delivered by #164) and `useAgentStatus` from the agent entity. It introduces no new attention categories, no new judgment rules, and performs no mutation of Issue state.
- The Dashboard `Attention` slot transitions from an empty placeholder to hosting the Hero.

## Capabilities

### New Capabilities
- `dashboard-attention-hero`: The self-adaptive two-state Hero view that renders shared attention items plus runner-down status on the Dashboard and offers direct navigate / Approve / Resume actions.

### Modified Capabilities
- `dashboard-shell`: The Attention zone-slot requirement changes from "renders as an empty placeholder with no zone content" to hosting the Attention Hero. The other three slots (`Pulse`, `Productivity`, `Digest`) remain empty placeholders pending their downstream issues.

## Impact

- **Code**: New web view under `packages/web/src` (dashboard page or dedicated widget) consuming `deriveAttentionItems` + `AttentionItem` from the Issue entity and `useAgentStatus` from the agent entity; `DashboardPage` wires the Hero into the `attention` slot in place of `DashboardZonePlaceholder`.
- **APIs**: Consumes existing approval and resume endpoints; introduces **no** new server endpoints or domain changes.
- **Dependencies**: Depends on #164 (done) for `deriveAttentionItems` / `AttentionItem` and #163 (done) for the Dashboard slot contract. No new third-party dependencies.
- **Specs**: Adds `specs/dashboard-attention-hero/spec.md`; records a delta against `dashboard-shell`.
- **Risk**: Low — pure composition UI. Judgment logic is owned by #164 and reused unchanged, so Hero output is guaranteed consistent with Kanban card badges.
