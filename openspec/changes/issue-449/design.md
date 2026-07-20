## Context

`RoutingDispatchHandler` currently evaluates a project routing table and calls `IAgentLauncher.LaunchAsync` with only `ProjectId`. `AgentLauncher` copies `AgentLaunchContext.WorkspacePath` to both `OpenAgentSessionCommand.WorkDir` and `AgentJobInput.WorkspacePath`, so the absent value survives until `AgentJobGrain.BuildDispatch` omits `variables.workspace`. The runner then rejects the work before opening a Runtime Session because `agent-job-executor.ts` requires `workspace.path`.

WorkflowRun already owns the authoritative, persisted `WorkspaceIdentity`; issue-backed events carry `workflowrunid` and/or `issue` lineage in their CloudEvent envelope. Routing match evaluation and prompt rendering are intentionally envelope-only, but execution context is a physical precondition resolved after a hit and does not participate in the match decision.

AgentJob failure reporting already persists an actionable reason and writes `failureReason` plus `failureCategory` to the AgentSession's terminal `session.closed` fact. The generic AgentSession summary drops the reason, and `mo agent session show` consequently renders only the category. Separately, `mo issue events` merges Issue and WorkflowRun events only, so a routed AgentSession terminal fact is not visible there even though session metadata already supports indexed issue, trigger-event, and trigger-rule labels.

The design must preserve these boundaries:

- WorkflowRun is the workspace identity authority; the runner remains responsible for physical workspace preparation and filesystem checks.
- Matching and prompt rendering remain envelope-only and shared with `mo routing test`.
- Agent remains a leaf context and receives resolved launch inputs rather than querying Workflow or Issue.
- AgentJob owns work lifecycle; AgentSession records conversation and terminal facts.
- Cross-domain event-feed assembly belongs to AgentOps, not Workflow or an Issue domain mutation.

## Goals / Non-Goals

**Goals:**

- Resolve a routed Agent's workspace from the triggering WorkflowRun, with issue-current-run fallback only when the event has no explicit workflow run id.
- Pass one workspace path consistently to AgentSession and AgentJob before runner dispatch.
- Record a stable, correlated AgentJob and AgentSession failure without runner dispatch when no workspace can be resolved.
- Preserve event/rule/issue correlation on routed sessions and expose failed routed outcomes through the issue event feed.
- Expose AgentJob failure reason and category as separate generic-session API and CLI values.
- Preserve first-writer idempotency for durable event redelivery.

**Non-Goals:**

- Changing routing expression, ordering, `continue`, prompt rendering, or dry-run semantics.
- Changing the manual Agent launch request or resolving an omitted manual workspace automatically.
- Changing Inline Agent workspace preparation or the `mohist/opencode` action contract.
- Weakening the runner's required-workspace validation or making the server inspect runner-local files.
- Redesigning AgentSession observability, adding a new AgentJob public resource, or backfilling historical routed sessions.
- Fixing Agent name resolution in `mo routing rule create --agent`.

## Decisions

### 1. Resolve routing execution context after rule selection

Add a narrow resolver in `Events/Subscriptions`, used by `RoutingDispatchHandler` only after `RoutingTableEvaluator` has produced an executable hit and rendered prompt.

Resolution order:

1. Parse project, issue, epic, and workflow-run lineage from the CloudEvent envelope.
2. If `workflowrunid` is present, load that run through `WorkflowQuerier.GetWorkspaceAsync`; this explicit run is authoritative.
3. Otherwise, when issue lineage exists, read the issue's retained/current WorkflowRun id through its narrow grain contract and load that run's workspace.
4. Return an `AgentLaunchContext` containing project, issue, epic, and the persisted `WorkspaceIdentity.Path`, or a typed unresolved result with an actionable reason.

An explicit workflow run id never falls back to the issue's newer run. Doing so could execute a delayed event in an unrelated workspace. Lookup/storage exceptions remain exceptions so durable dispatch retries and dead-letters transient infrastructure failures; a successfully completed lookup that finds no run or workspace is a durable routed failure.

The resolver does not derive paths from `MohistWorkspaceLayout`, inspect the filesystem, or alter the event passed to matching and rendering.

**Alternatives considered:**

- Resolve inside `AgentLauncher`: rejected because it creates Agent-to-Workflow/Issue dependencies and changes the shared manual-launch path.
- Derive the path from the workflow run id: rejected because layout convention is not the persisted workspace authority and cannot distinguish an uninitialized run.
- Put workspace data into the routing matcher: rejected because execution state must not affect deterministic match or dry-run results.

### 2. Represent unresolved workspace as an idempotent AgentJob terminal outcome

Extend the shared launch pipeline with an explicit routed-failure entry point. It performs the same stable identity, Agent snapshot, AgentSession open, metadata, and trigger-label composition as a normal routed launch, then invokes a new idempotent AgentJob grain command that persists the input directly in `Failed` state and closes the AgentSession without scheduling or assigning runner work. Use a stable category such as `workspace-unavailable` and a reason naming the missing run/workspace context.

The grain command must atomically persist its own input and terminal state before appending the cross-aggregate `session.closed` fact. Redelivery uses the existing `(projectId, eventId, ruleId)` stable session/job keys. If the job already has input, its first persisted normal or failed outcome wins; the retry must not replace a running workspace or turn a preflight failure into a later launch. A repeated failed command must still ensure the idempotently correlated close fact exists, covering a failure between the AgentJob save and AgentSession append.

`SubmitAsync` followed by `FailAsync` is not used because submission immediately attempts dispatch and races the failure. The existing `FailAsync(reason)` is not sufficient for this path because it does not persist launch input or close the associated AgentSession.

**Alternatives considered:**

- Throw and rely on dispatcher dead-lettering: rejected because the operator sees no routed AgentSession outcome and issue-level traceability remains absent.
- Open only a failed AgentSession: rejected because every executable routing hit is modeled as Agent-owned work and must retain an AgentJob outcome.
- Submit a workspace-less job and let the runner reject it: rejected because it preserves the current contract disagreement and consumes runner capacity for invalid server-composed work.

### 3. Preserve routed lineage on AgentSession metadata

Normal and failed routed launches pass issue and epic lineage from the event into `AgentLaunchContext`, in addition to the existing trigger event and rule labels. `GenericAgentSessionMetadata` already maps these values to persisted labels and the database already has computed columns for issue, trigger event, and trigger rule, so no schema change is required.

The first persisted launch context is canonical on redelivery. A later issue-only lookup that resolves a different current run must not overwrite the existing session work directory or workspace/issue trigger metadata.

**Alternatives considered:**

- Resolve issue lineage later from the trigger event for every read: rejected because it adds repeated cross-store joins and cannot repair an event that has been retained or deleted differently from the session.
- Add a separate routing-correlation table: rejected because session labels already provide stable indexed correlation and a second write model would duplicate facts.

### 4. Project failure reason and category from one terminal fact

Extend the internal `TerminalFact` projection to parse `failureCategory` alongside its existing `failureReason`, status, completion time, and exit code. `GetGenericSessionSummaryAsync` uses the latest applicable terminal fact for both failure fields and adds `FailureReason` to `GenericAgentSessionSummaryDto`; transcript summary projection remains responsible for model and tool counts.

Taking both fields from the same terminal fact prevents a current failure reason from being paired with a category left by an older Runtime Session lineage entry. The API addition is nullable and additive. `RenderAgentSessionShow` prints distinct `failure reason` and `failure category` rows. JSON mode requires no special transformation because it already emits the server payload.

AgentJob failure paths used by this change must all close the generic AgentSession through the shared terminal helper. The new no-dispatch failure command uses that helper; existing dispatch-exhaustion and report-timeout paths already do. Any forced AgentJob failure path covered by generic-session visibility should be converged on the same helper rather than persisting job state alone.

**Alternatives considered:**

- Add `failureReason` to `TranscriptEventSummaryProjector` and keep category from its independent scan: rejected because independently reduced values can come from different terminal attempts.
- Query AgentJob state from the session API: rejected because AgentSession already owns the persisted terminal observation and the read would introduce a reverse lookup from session to work owner.
- Replace `failureCategory` with the reason: rejected because category remains useful for machine grouping while reason is operator-facing evidence.

### 5. Assemble routed failures into the issue feed in AgentOps

Add an issue-scoped assembler under `AgentOps/Services`. It combines:

- Issue events from `IEventStore`;
- WorkflowRun events through `WorkflowEventQuerier.ListWorkflowEventsAsync`, preserving Workflow's invalidated-control-event filtering;
- failed `session.closed` transcript parts for generic AgentSessions matching project, agent-launch issue label, and non-empty trigger event/rule labels.

The assembler merges all sources chronologically and applies the requested limit after the merge. A projected routed failure uses the existing event response shape with `type: "session.closed"`, the canonical AgentSession source/subject, and data containing status, session id, Agent id/name, failure reason/category, trigger event id, and trigger rule id. This is a read projection only; it does not append an Issue event or copy Session authority into another aggregate.

`WorkflowEventRoutes` delegates the issue endpoint to this AgentOps assembler. `WorkflowEventQuerier` keeps WorkflowRun-specific selection and invalidation logic but no longer owns cross-domain issue-feed composition. The CLI already prints issue events as returned JSON, so no new CLI event renderer is needed.

Only failed routed sessions are added by this change. Manual sessions, successful routed sessions, sessions for another project/issue, and sessions without both trigger labels are excluded.

**Alternatives considered:**

- Query Session rows directly from `WorkflowEventQuerier`: rejected by Workflow's zero-business-context-dependency invariant.
- Append a durable Issue event when AgentSession closes: rejected because it duplicates a Session fact and introduces a cross-aggregate mutation solely for a read view.
- Filter the project-wide activity feed in memory: rejected because unrelated project events can consume the limit before issue filtering, and its current synthetic lifecycle envelope omits trigger correlation.

### 6. Verify at product and ownership boundaries

Focused coverage will include:

- routing dispatch composition for explicit WorkflowRun workspace, issue-only fallback, and unresolved workspace;
- no runner assignment for a pre-dispatch failure and idempotent replay with one job/session/close fact;
- first-writer behavior when issue-current-workspace resolution changes between deliveries;
- AgentJob grain terminal persistence and close retry behavior;
- generic session summary/API reason-category separation, successful omission, and latest-terminal-fact selection;
- AgentOps issue-feed inclusion and exclusion matrix plus API envelope shape;
- CLI table and JSON output for generic session failures;
- existing runner missing-workspace validation as a defensive contract, manual launch behavior, Inline Agent behavior, and architecture dependency tests.

## Risks / Trade-offs

- `[Risk] An explicit event references an old or missing WorkflowRun while the issue now has a newer run` -> Treat explicit `workflowrunid` as authoritative and fail visibly; never fall forward to unrelated work.
- `[Risk] Issue-only event redelivery resolves a different current workspace` -> Stable event/rule job identity and first-writer semantics preserve the original persisted launch outcome.
- `[Risk] AgentJob terminal save succeeds but AgentSession close append fails` -> Make repeated terminal submission re-attempt the idempotently correlated `session.closed` append.
- `[Risk] Persisted workspace identity points to a directory already cleaned on the runner` -> Keep filesystem authority on the runner; the turn may fail with an actionable runtime/workspace reason, but it will no longer fail because dispatch omitted the path.
- `[Risk] New session events alter issue-feed limit results for existing clients` -> Merge before applying the limit, retain chronological ordering and the existing envelope shape, and add only failed routed outcomes required by the spec.
- `[Risk] Historical routed sessions lack issue labels` -> Apply visibility forward only; avoid an unreliable backfill from retained event data.
- `[Trade-off] Workspace resolution adds one Workflow read and sometimes one Issue read per routing hit` -> Perform reads only after a rule is selected, use the explicit WorkflowRun fast path, and keep dry-run free of these reads.

## Migration Plan

1. Add the routing launch-context resolver, no-dispatch failed AgentJob command, shared launch composition, and tests. Keep the runner workspace requirement unchanged.
2. Add issue/epic metadata to routed launch contexts and the AgentOps issue-feed assembler; switch the issue events API to the assembler while preserving WorkflowRun event filtering.
3. Add `FailureReason` to the generic AgentSession DTO/query and update CLI rendering and contract tests.
4. Deploy the server before or together with the CLI. Older CLIs ignore the additive summary field; the updated CLI against an older server simply has no reason to render.
5. No database migration or historical backfill is required. Existing AgentJob state and `session.closed` JSON shapes remain readable.

Rollback is code-only. The new failed jobs and terminal facts use existing persisted state fields and remain readable by the previous server; rolling back removes the additional issue-feed projection and summary field but does not require data cleanup. The runner requires no deployment or rollback change.

## Open Questions

None. The triggering event's explicit WorkflowRun is the workspace authority; issue-current-run lookup is used only when that explicit lineage is absent, and workspace resolution occurs after envelope-only routing selection.
