# Self-Review — Issue #490 (评论提及 / comment mention), round 2

**Reviewer:** plan-stage self-review (reviewer, not fixer)
**Artifacts reviewed:** `proposal.md`, `design.md`, `tasks.json`, `specs/issue-comment-event/spec.md`,
`specs/comment-mention/spec.md`, cross-checked against issue #490, the fixed upstream contract
`design/agent-mentions.md`, and the live code (`IssueGrain.AddCommentAsync`, `IAgentLauncher`,
`RoutedAgentLaunchContextResolver`, `AgentSessionResolver`, `IssueLineage`, `RoutingDispatchHandler`).
**Verdict:** **PASS** — the round-1 blocker (F1) is fully resolved and the fix introduced no new
contradictions; the plan is internally consistent and ready to build. One low-severity informational
note remains (non-blocking).

## Round-1 findings — resolution check

- **F1 (launch-path spec↔design contradiction) — RESOLVED.** The blocking mismatch is gone in all
  four artifacts:
  - `specs/comment-mention/spec.md`: the contradicting *Launch-path reuse and provenance* requirement
    is replaced by **Workspace-optional launch, regardless of run state** ("SHALL use the shared
    launcher's manual path — NOT the routed path … MUST NOT apply a workspace/preflight gate … no
    preflight failure … when the issue has no active workflow run or its run is terminal") with
    backlog-issue and terminal-run scenarios, plus a separate *Launch provenance* requirement. This
    now matches design Decision 1 and T-002 ("launches on a backlog issue … without any preflight
    failure").
  - `proposal.md`: "What Changes", the `comment-mention` capability, and both Impact sections now
    state the manual, workspace-optional path and explicitly note the handler does NOT use
    `LaunchRoutedAsync`/the workspace resolver.
  - The only remaining "routed" mentions in `design.md` are inside Decision 1's reconciliation
    analysis (quoting/reconciling the upstream wording) — not live contradictions.
- **Extra D2 contradiction (caught during the fix) — RESOLVED.** The proposal's Issue-aggregate Impact
  previously said the `IssueEvent` family + serializer "gains comment-added" (implying a union
  variant), contradicting design D2. It now states a standalone `CloudEvent` append with the union and
  `IssueEventSerializer` unchanged — consistent with D2 and T-001.
- **F1 part 2 (upstream-doc reconciliation) — RESOLVED.** T-002 now lists reconciling
  `design/agent-mentions.md`'s "reuse routed pipeline / workspace / preflight" wording as an explicit
  deliverable (output + an acceptance criterion).
- **N1 (broken relative links) — RESOLVED.** `proposal.md` and `design.md` now use `../../../design/…`
  and `../../../docs/…`, which resolve correctly (verified).
- **N2 (mute open question) — RESOLVED.** Promoted to a firm **Decision 7** (an explicit `@` overrides
  `muted`; `MentionDispatchHandler` does not consult `WatchEntryStore`), with rationale + a rejected
  alternative. Removed from Open Questions; T-002 notes and an acceptance criterion reference it. The
  plan is now buildable without sign-off.

## Fresh re-review — no new blockers

I re-checked the updated artifacts for contradictions introduced by the fixes and for anything missed
in round 1:

- **Launch-path consistency (the former blocker).** The manual workspace-optional decision is now
  stated identically in spec, design D1, proposal, and T-002 (13 consistent references). A backlog or
  terminal-issue mention launches with no preflight failure everywhere it is described. ✓
- **Decision→spec→task coverage.** Every design decision maps to a spec requirement and a task: D1→
  *Workspace-optional launch* / T-002; D2→*Comment-added event emission* / T-001; D3→*Per-comment
  launch idempotency* / T-002; D4→*Token parsing* / T-002; D5→*Loop prevention* / T-002; D6→*Launch
  provenance* / T-002; D7→T-002 AC. ✓
- **Idempotency is consistent end-to-end** and independent of the launch path: `(projectId,
  commentId, agentId)` in spec == design D3 == T-002, via a new `AgentSessionResolver` stable key. ✓
- **Mechanism correctness of the manual path.** Verified against code: `AgentLaunchContext.WorkspacePath`
  is optional and `LaunchAsync` opens a session with a null workdir, so a workspace-free launch on a
  backlog issue is a supported configuration; the Agent then creates the run/workspace itself via
  `mo issue start`. The comment-anchored stable key reuses the existing `StableId` hashing helper. ✓
- **Event-emission capability accuracy.** Verified: `AddCommentAsync` emits nothing today; the
  `EpicGrain` direct-`IEventStore.AppendAsync` pattern is the stated precedent; `IssueLineage.
  BuildExtensions` stamps `projectid`/`issue`/`epic`. Spec, design D2, proposal, and T-001 all agree. ✓
- **DAG / format.** `tasks.json` is valid JSON; T-001 (pri 1) → T-002 (pri 2, deps `[T-001]`), acyclic,
  strictly-lower-priority deps; T-002 has 11 acceptance criteria. Every spec scenario uses exactly four
  hashtags; every requirement has ≥1 scenario; no `ADDED/MODIFIED/REMOVED` headers; no cross-spec
  references. ✓
- **Task split.** Capability-aligned (event source / mention dispatch); launcher-extension + handler
  correctly merged (handler is the extension's only caller); no over-granular steps; no standalone test
  task; no spurious human-surface task (proposal correctly scopes out new CLI/API/Web). ✓

## Non-blocking note (informational)

- **N4 — Spec is silent on the mute interaction.** The `comment-mention` spec has no scenario asserting
  that a `muted` Agent is still launched when `@`-mentioned; the behavior is covered only by design D7
  and a T-002 acceptance criterion. This is not a contradiction (the spec says the mention launches, and
  D7 establishes mute does not change that), so it is non-blocking. Optionally add a *Mention launches
  despite mute* scenario for explicitness during the build task.
- **N3 (carried over) — case-insensitivity is an implementation responsibility.** Specs mandate
  case-insensitive token resolution and author comparison; `AgentQuerier.GetByNameAsync` is a direct
  `==` query today. Correctly flagged as a build-time note in T-002; no plan change needed.

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
| comment-mention: Workspace-optional launch, regardless of run state | T-002 | ✓ |
| comment-mention: Launch provenance | T-002 | ✓ |

<promise>PASS</promise>
