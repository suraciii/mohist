# Self-Review — Issue #490 (评论提及 / comment mention)

**Reviewer:** plan-stage self-review (reviewer, not fixer)
**Artifacts reviewed:** `proposal.md`, `design.md`, `tasks.json`,
`specs/issue-comment-event/spec.md`, `specs/comment-mention/spec.md`, cross-checked against issue #490
and the fixed upstream contract `design/agent-mentions.md`, and against the live code
(`IssueGrain.AddCommentAsync`, `IAgentLauncher`, `RoutedAgentLaunchContextResolver`,
`AgentSessionResolver`, `IssueLineage`, `RoutingDispatchHandler`).
**Verdict:** **FAIL** — one blocking inconsistency between `specs/` and `design.md`/`tasks.json` on the
mention launch path. The plan is otherwise sound; this single contradiction must be reconciled before
build.

## Blocking finding

### F1 — Spec and design/tasks disagree on the launch path (preflight vs. workspace-optional)

`specs/comment-mention/spec.md` → **Requirement: Launch-path reuse and provenance** states:

> "A mention launch SHALL reuse the routed launch pipeline: issue context resolution, **workspace
> resolution, preflight validation, and preflight-failure handling (a preflight failure records a
> failed AgentJob) SHALL behave identically to a routing-rule launch**."

with scenario *"Mention reuses workspace and preflight handling"* → "workspace resolution, preflight
validation, and preflight-failure recording behave the same as a routing-rule launch".

`design.md` **Decision 1** and `tasks.json` T-002 state the **opposite**: the mention launch "MUST use
the manual-style path (workspace-optional)" and "launches on a backlog issue (no workflow run)
**without any preflight failure**", explicitly forbidding `RoutedAgentLaunchContextResolver` /
`LaunchRoutedAsync` because they "require a nonterminal run+workspace and would preflight-fail exactly
the backlog case the feature exists to serve".

These cannot both hold. Under the spec, a `@supervisor` comment on a **backlog** issue records a
preflight-failed AgentJob and no Agent runs; under the design/T-002 that same comment launches the
Agent successfully. A builder has no way to know which behavior to implement or test.

**Why the design is the correct side (so the spec must move):** the routed path requires a nonterminal
workflow run with a persisted workspace (`RoutedAgentLaunchContextResolver` returns `IssueRunMissing`
for an issue with no run). The upstream contract's own headline example is `@supervisor 监督并推进这个issue`
→ the Agent runs `mo issue start 42` — only possible if the Agent launches without a workspace and then
creates the run itself. The upstream pseudocode also specifies the idempotency key as
`hash(projectId, commentId, agentId)` (a comment-anchored key the routed path does not natively
produce). Both the example and the key point to the workspace-optional manual path; only the single
"复用路由启动的解析管线 … workspace 解析" sentence points the other way. So design Decision 1 is the
better-supported reading, and the change-level spec (plus the upstream doc's wording) must be
reconciled to match.

**Recommended fix (for the separate fix task, not here):**
1. Rewrite `specs/comment-mention/spec.md#Launch-path reuse and provenance` to drop "workspace
   resolution, preflight validation, and preflight-failure handling behave identically to a
   routing-rule launch". Replace with: the launch reuses the shared launcher and records issue context
   as session metadata; it is **workspace-optional with no preflight gate**, so a mention launches
   regardless of workflow-run state (backlog / in-progress / terminal). Rewrite the *Mention reuses
   workspace and preflight handling* scenario accordingly (e.g. *Mention launches without a workspace*:
   WHEN a mention targets an issue with no active run THEN the Agent launches successfully with no
   preflight failure). Keep the provenance half (commentId + event-id trigger labels) as-is — it is
   consistent with design D6 and T-002.
2. Have T-002 (or a doc note in it) also reconcile the wording in the upstream
   `design/agent-mentions.md` ("复用路由启动的解析管线 … workspace 解析 … preflight") to the
   workspace-optional decision, per AGENTS.md's差距-footnote convention — the plan currently asserts
   the reconciliation but does not make the upstream-doc edit an explicit deliverable.

## Non-blocking notes (informational)

- **N1 — Broken relative doc links.** `proposal.md` and `design.md` reference
  `../../design/agent-mentions.md` and `../../docs/agents.md`, which resolve to `openspec/design/…`
  (nonexistent); the correct depth is `../../../design/…`. Flagged only as informational: this matches
  the convention the merged issue #489 used (`../../design/issue-watch.md`) and review tolerated there,
  so it is not treated as blocking here.
- **N2 — Mute interaction is an open question with a buildable default.** `design.md` Open Questions
  leaves mute-vs-mention open; T-002 encodes the recommended default (an explicit `@` overrides mute;
  `MentionDispatchHandler` does not consult `WatchEntryStore`), so the task is buildable as-is. Worth a
  quick human sign-off before build, but not blocking — the default is clearly stated and reversible.
- **N3 — Case-insensitivity relies on implementation, not a stored invariant.** Specs mandate
  case-insensitive token resolution and loop-prevention author comparison; `AgentQuerier.GetByNameAsync`
  is a direct `==` query today. This is correctly called out as an implementation responsibility in
  T-002; no plan change needed, just a build-time note.

## Fresh re-review — consistency checks that passed

- **Spec format.** Every requirement has ≥1 scenario; all scenarios use exactly four hashtags; no
  `ADDED/MODIFIED/REMOVED` headers; specs are self-contained with no cross-spec references. ✓
- **Idempotency is consistent end-to-end.** spec *Per-comment launch idempotency* `(projectId,
  commentId, agentId)` == design D3 == T-002; and it is independent of (and cleaner under) the
  workspace-optional path. ✓
- **Event-emission capability is accurate and self-consistent.** spec `issue-comment-event` (emission +
  lineage stamping via `IssueLineage.BuildExtensions` + comment-only source) matches design D2
  (direct `IEventStore` append in `AddCommentAsync`, not an `IssueEvent` union variant) and T-001.
  Verified against code: `AddCommentAsync` emits nothing today; `EpicGrain` provides the direct-emit
  precedent; `IssueLineage.BuildExtensions` stamps `projectid`/`issue`/`epic`. ✓
- **Loop prevention, resolution-failure no-op, one-shot, token parsing** are consistent across spec ↔
  design D4/D5 ↔ T-002 and match the upstream contract. ✓
- **DAG / dependencies.** T-001 (pri 1, no deps) → T-002 (pri 2, deps `[T-001]`); valid, acyclic,
  strictly-lower-priority deps. `tasks.json` is valid JSON; every task has ≥5 verifiable acceptance
  criteria including test + warning-clean build. ✓
- **Task split.** Capability-aligned (event source / mention dispatch); the launcher-extension +
  handler are correctly merged into one task (the handler is the extension's only caller); no
  over-granular technical-step tasks; no standalone test tasks; no spurious human-surface task (the
  proposal correctly scopes out new CLI/API/Web). ✓

## Coverage check — spec requirements → tasks

| Capability / requirement | Covered by | Status |
|---|---|---|
| issue-comment-event: Comment-added event emission | T-001 | ✓ |
| issue-comment-event: Issue lineage stamping | T-001 | ✓ |
| issue-comment-event: Comments are the only trigger source | T-001 | ✓ |
| comment-mention: Mention-triggered launch | T-002 | ✓ |
| comment-mention: Token parsing | T-002 | ✓ |
| comment-mention: Loop prevention | T-002 | ✓ |
| comment-mention: Resolution failure is a no-op | T-002 | ✓ |
| comment-mention: Per-comment launch idempotency | T-002 | ✓ |
| comment-mention: One-shot launch, no persistent subscription | T-002 | ✓ |
| comment-mention: Launch-path reuse and provenance | T-002 | ✗ **contradicts design/T-002 (F1)** |

<promise>FAIL</promise>
