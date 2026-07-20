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

- Resolve a routed Agent's workspace from an ownership-validated triggering WorkflowRun, with issue-current-run fallback only when the event has no explicit workflow run id and the bound run is nonterminal.
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
2. If `workflowrunid` is present, load a narrow routing execution context containing run id, project id, issue/epic lineage, status, and `WorkspaceIdentity`; this explicit run is authoritative.
3. Validate that the run belongs to the envelope project and that any envelope issue/epic values match the run. When the envelope omits issue/epic, carry the run's values forward for Session correlation.
4. Otherwise, when issue lineage exists, read the issue's currently bound WorkflowRun id, load the same narrow run context, and accept it only while the run is nonterminal.
5. Require a non-null `WorkspaceIdentity` with a non-whitespace path. Return an `AgentLaunchContext` containing the validated project, issue, epic, and path, or a typed unresolved result naming the missing run, lineage mismatch, terminal/stale issue binding, or empty path.

An explicit workflow run id never falls back to the issue's newer run. Doing so could execute a delayed event in an unrelated workspace. An issue-only event never reuses a terminal retained run because its runner-local directory may already be cleanup-eligible. Lookup/storage exceptions remain exceptions so durable dispatch retries and dead-letters transient infrastructure failures; a successfully completed lookup that finds no valid context is a durable routed failure.

The resolver does not derive paths from `MohistWorkspaceLayout`, inspect the filesystem, or alter the event passed to matching and rendering.

**Alternatives considered:**

- Resolve inside `AgentLauncher`: rejected because it creates Agent-to-Workflow/Issue dependencies and changes the shared manual-launch path.
- Derive the path from the workflow run id: rejected because layout convention is not the persisted workspace authority and cannot distinguish an uninitialized run.
- Put workspace data into the routing matcher: rejected because execution state must not affect deterministic match or dry-run results.
- Read only `WorkspaceIdentity` by run id: rejected because it cannot validate project/issue ownership and could route an Agent into another project's workspace.

### 2. Represent unresolved workspace as an idempotent AgentJob terminal outcome

Extend the shared launch pipeline with a routed preparation protocol. `AgentLauncher` composes one `RoutedAgentLaunchPlan` containing the AgentJob snapshot, complete Session open command, trigger metadata, and either `Executable` or `PreflightFailed(reason, category)` disposition. `IAgentJobGrain.EnsurePreparedAsync` persists the first plan under the existing stable event/rule job key and returns that canonical plan on every replay. A prepared job is not dispatchable and `OnActivateAsync` does not auto-dispatch it. The launcher durably opens the AgentSession from the returned canonical plan, then calls `ActivatePreparedAsync`; activation either enables normal dispatch or terminalizes the job without assigning Runner work. Use category `workspace-unavailable` for invalid execution context.

Persisting the plan before Session open is the first-writer fence: an unresolved first delivery cannot acquire a later workspace, and an executable first delivery cannot be redirected by changed issue state. A crash between plan persistence, Session open, and activation leaves the durable event unacknowledged; redelivery resumes the same plan. Existing manual launches retain their strict random-id submission path and do not use routing preparation.

Every AgentJob terminal transition uses one durable delivery protocol. Before saving terminal state, the grain stores a `PendingSessionClose` payload containing a stable delivery id (`agent-job:{jobKey}:terminal`), status, exit code, failure reason/category, and a single recorded timestamp, and registers a durable Orleans reminder. It then saves terminal state and attempts delivery. `ReportResultAsync` for an already-terminal job repairs a pending close before returning rather than rejecting it immediately; activation and reminder ticks do the same.

Add an AgentSession terminal command keyed by the stable delivery id. The command detects an already-persisted delivery across all Session turns, otherwise appends the terminal fact and synchronously flushes Session state/events and transcript before acknowledging. It throws on persistence failure. After acknowledgement, AgentJob clears `PendingSessionClose`, saves, and unregisters the reminder. A crash after Session commit but before clearing the AgentJob marker causes an idempotent retry; a crash before Session commit leaves the reminder active. The same protocol covers normal reports, preflight failure, dispatch exhaustion, report timeout, and forced failure.

`SubmitAsync` followed by `FailAsync` is not used because submission immediately attempts dispatch and races the failure. The existing fire-and-forget Session append is not an acknowledgement boundary because it can return before transcript flush; terminal delivery uses the new synchronous command instead.

**Alternatives considered:**

- Throw and rely on dispatcher dead-lettering: rejected because the operator sees no routed AgentSession outcome and issue-level traceability remains absent.
- Open only a failed AgentSession: rejected because every executable routing hit is modeled as Agent-owned work and must retain an AgentJob outcome.
- Submit a workspace-less job and let the runner reject it: rejected because it preserves the current contract disagreement and consumes runner capacity for invalid server-composed work.
- Open Session before claiming the idempotent AgentJob: rejected because Session merge semantics can accept a later workspace while AgentJob keeps the first input, producing divergent audit and execution state.
- Rely on report/event redelivery without a pending-close reminder: rejected because AgentJob terminal state currently short-circuits report replay and no future activation is guaranteed.

### 3. Preserve routed lineage on AgentSession metadata

Normal and failed routed launches pass issue and epic lineage from the event into `AgentLaunchContext`, in addition to the existing trigger event and rule labels. `GenericAgentSessionMetadata` already maps these values to persisted labels and the database already has computed columns for issue, trigger event, and trigger rule, so no schema change is required.

The first persisted launch plan is canonical on redelivery. AgentSession is always opened from the plan returned by `EnsurePreparedAsync`, never from the caller's newly resolved values, so a later issue-only lookup cannot overwrite work directory or workspace/issue trigger metadata.

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
- `[Risk] AgentJob terminal save succeeds but AgentSession close persistence fails` -> Persist `PendingSessionClose`, keep a durable reminder until synchronous Session acknowledgement, and retry the stable delivery id across activation loss and report replay.
- `[Risk] Persisted workspace identity points to a directory already cleaned on the runner` -> Keep filesystem authority on the runner; the turn may fail with an actionable runtime/workspace reason, but it will no longer fail because dispatch omitted the path.
- `[Risk] New session events alter issue-feed limit results for existing clients` -> Merge before applying the limit, retain chronological ordering and the existing envelope shape, and add only failed routed outcomes required by the spec.
- `[Risk] Historical routed sessions lack issue labels` -> Apply visibility forward only; avoid an unreliable backfill from retained event data.
- `[Trade-off] Workspace resolution adds one Workflow read and sometimes one Issue read per routing hit` -> Perform reads only after a rule is selected, use the explicit WorkflowRun fast path, and keep dry-run free of these reads.

## Migration Plan

1. Add the durable AgentJob pending-close/reminder protocol and synchronous idempotent AgentSession terminal command; migrate every AgentJob terminal path and verify persistence-failure recovery.
2. Add the ownership-validating routing launch-context resolver and prepared-launch first-writer fence, then switch routed launch to prepare -> canonical Session open -> activate. Keep the runner workspace requirement unchanged.
3. Add issue/epic metadata to routed launch contexts and the AgentOps issue-feed assembler; switch the issue events API to the assembler while preserving WorkflowRun event filtering.
4. Add `FailureReason` to the generic AgentSession DTO/query and update CLI rendering and contract tests.
5. Deploy the server before or together with the CLI. Older CLIs ignore the additive summary field; the updated CLI against an older server simply has no reason to render.
6. No relational database migration or historical backfill is required. New AgentJob/AgentSession state fields are additive JSON/Orleans serializer fields; existing state and `session.closed` JSON shapes remain readable.

Rollback is code-only. The new failed jobs and terminal facts use existing persisted state fields and remain readable by the previous server; rolling back removes the additional issue-feed projection and summary field but does not require data cleanup. The runner requires no deployment or rollback change.

## Open Questions

None. The triggering event's ownership-validated explicit WorkflowRun is authoritative; issue-current-run lookup is used only when explicit lineage is absent and only for a nonterminal bound run. Workspace resolution occurs after envelope-only routing selection, and routed launch identity is fenced before Session open.
