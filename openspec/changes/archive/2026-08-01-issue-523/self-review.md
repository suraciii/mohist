# Self-Review — Issue 523: 在 Web 中跟进和操作 Agent Session

## Proposal

The proposal defines a clear problem (Web must make recorded AgentSession facts intelligible and actionable), two capability boundaries (`agent-session-web-tracking`, `agent-session-web-operations`), and an accurate impact scope (Web data sources/widgets, Server read projections, command APIs). It aligns with the product vision: Web is a fallback observation/control surface, not a second runtime.

**No issues.**

## Specs

### Format compliance

Both spec files use `### Requirement:` headers and `#### Scenario:` headers with WHEN/THEN format. Every requirement has at least one scenario. All scenarios use exactly 4 hashtags. SHALL/MUST normative language is used throughout. No `## ADDED/MODIFIED/REMOVED` delta headers. Specs are self-contained and do not reference other spec files.

**No issues.**

### Coverage

- `agent-session-web-tracking`: 3 requirements, 6 scenarios — covers source/context identification, input/turn/transcript evidence, and authoritative/unknown state convergence.
- `agent-session-web-operations`: 4 requirements, 8 scenarios — covers follow-up, turn control (cancel/stop), context maintenance (compact/reset), and command-outcome convergence.

All seven spec requirements are covered by at least one task.

**No issues.**

## Design

### Accuracy verified against codebase

- `UnifiedSessionSummaryDto` (`AgentSessionReadModels.cs:341-358`) does lack `failureCategory`, `failureReason`, `toolCallCount`, `toolErrorCount`, `recoveryAvailable`, and `currentTurnId` — all present in `GenericAgentSessionSummaryDto` but absent from the unified DTO. Design's claim that the projection "does not yet carry every fact required" is accurate.
- `ResolveCanonicalFollowupTargetAsync` and `ResolveCancelTargetAsync` (`AgentSessionQuerier.cs:305-373`) branch on source-kind and accept both `agent-launch` and `workflow`. Design's claim that canonical command APIs already work for both sources is accurate.
- Three Web Session detail routes exist today (`App.tsx:72,73,78`): two Issue-scoped (`SessionPage`) and one generic (`GenericSessionPage`). Design's claim of duplicated data sources is accurate.

### Architecture soundness

The design makes four well-reasoned decisions, each with an explicit rejected alternative:
1. One canonical `/sessions/:sessionId` route and `useUnifiedSessionDataSource` — avoids duplicating state mapping across sources.
2. Extended `UnifiedSessionSummaryDto` as the sole read contract — avoids stale cross-query joins.
3. Reuse canonical Session-ID command APIs — avoids source-branched dispatch.
4. Read/live convergence over optimistic state — preserves unknown-state safety.

All decisions respect the architecture boundary: Web is presentation only, Server/Session domain remains the single state authority.

**No issues.**

## Tasks

### DAG validity

Linear chain: T-001 (priority 1, no deps) → T-002 (priority 2, depends T-001) → T-003 (priority 3, depends T-002). Acyclic, all dependencies point to strictly lower priority. ✓

### Task-spec alignment

- T-001 → `agent-session-web-tracking#session-context-and-current-state` (completes the read projection)
- T-002 → `agent-session-web-tracking#input-turn-and-transcript-evidence` (delivers the unified tracking page)
- T-003 → `agent-session-web-operations#continue-a-session-with-follow-up-input` (connects all operations)

T-003 covers all four operations spec requirements via its acceptance criteria (cancel/stop rules, compact/reset gating, convergence). The single spec anchor to the follow-up requirement is acceptable since the acceptance criteria enumerate every operation.

### Split quality

Each task is a complete vertical slice: T-001 delivers a usable Server API, T-002 delivers a usable tracking page, T-003 adds controls to that page. No over-granular technical steps. Each task includes its test coverage.

**No issues.**

## Minor observations (non-blocking)

These are implementation details that do not create spec ambiguities or missing requirements:

1. **Sibling navigation for Workflow sources**: `useIssueSessionDataSource` currently populates `siblingNav`/`siblingSidebar` via `useSiblingSessions`; `useGenericSessionDataSource` sets them null. The design does not explicitly state how the unified data source handles sibling navigation for Workflow Sessions. The unified summary carries `workflowRunId`, so sibling fetching remains feasible. No spec requires sibling navigation.

2. **Input attachment support**: `useIssueSessionDataSource` sets `supportsInputAttachments: false`; the generic source sets it `true`. The unified data source will need to resolve this per source or runtime. No spec requirement mandates attachment support for either source.

3. **Doc footnote update**: `docs/web-ui.md:191-194` has an实装差距 footnote stating the AgentSession page gaps ("对应实施 issue 待从 AgentSession spec 创建"). No task explicitly updates this footnote, but the docs already describe the target behavior; footnote cleanup is natural delivery work.

## Verdict

The plan is coherent, accurate against the codebase, and ready to build. All capabilities have specs, all spec requirements have task coverage, the DAG is valid, and the design decisions are sound with explicit alternatives considered.

<promise>PASS</promise>
