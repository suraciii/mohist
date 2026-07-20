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

Extend the shared launch pipeline with a routed preparation protocol. `AgentLauncher` composes one `RoutedAgentLaunchPlan` containing the AgentJob snapshot, complete immutable Session open command, trigger metadata, and either `Executable` or `PreflightFailed(reason, category)` disposition. `IAgentJobGrain.EnsurePreparedAsync` registers the durable `agent-job-recovery` reminder before persisting the first plan under the existing stable event/rule job key, then returns that canonical plan on every replay. A reminder that finds no persisted recovery fence unregisters itself, covering reminder registration followed by failed state persistence.

`AdvancePreparedLaunchAsync` is invoked on the fast path after preparation and from both `OnActivateAsync` and the recovery reminder. It reads only the persisted plan; it never reloads a routing rule or live Agent definition. It calls a routed Session open command that creates the Session once or validates the exact persisted source labels, metadata, and work directory on replay. For an executable plan, Session acknowledgement is followed by a persisted `LaunchReady` transition; only then may normal dispatch run. For a preflight-failed plan, Session acknowledgement is followed by the durable terminal-delivery protocol. The recovery reminder remains registered until the prepared fence has become `LaunchReady` or `PendingSessionClose`; a stale tick with no recovery fence unregisters itself.

Persisting the plan before Session open is the first-writer fence and the durable recovery handoff: an unresolved first delivery cannot acquire a later workspace, an executable first delivery cannot be redirected by changed issue state, and a crash does not depend on event redelivery. If redelivery occurs, it may call `EnsurePreparedAsync` again, but subsequent rule archival, rule edits, or Agent deactivation cannot prevent AgentJob's reminder/activation path from completing the claimed plan. Existing manual launches retain their strict random-id submission path and do not use routing preparation.

Every AgentJob terminal transition uses one durable delivery protocol. Before saving terminal state, the grain stores a `PendingSessionClose` payload containing a stable delivery id (`agent-job:{jobKey}:terminal`), status, exit code, failure reason/category, and a single recorded timestamp, and registers a durable Orleans reminder. It then saves terminal state and attempts delivery. `ReportResultAsync` for an already-terminal job repairs a pending close before returning rather than rejecting it immediately; activation and reminder ticks do the same. A reminder tick that reloads state with no pending delivery unregisters the orphan reminder, covering reminder registration followed by failed terminal-state persistence.

Add an AgentSession terminal command keyed by the stable delivery id. The command writes that id into the terminal payload and uses it verbatim as the transcript part correlation key; it detects an already-persisted delivery across all Session turns, otherwise appends the terminal fact and synchronously flushes Session state/events and transcript before acknowledging. It throws on persistence failure. After acknowledgement, AgentJob clears `PendingSessionClose`, saves, and unregisters the reminder. A crash after Session commit but before clearing the AgentJob marker causes an idempotent retry; a crash before Session commit leaves the reminder active. The same protocol covers normal reports, preflight failure, dispatch exhaustion, report timeout, and forced failure.

`SubmitAsync` followed by `FailAsync` is not used because submission immediately attempts dispatch and races the failure. The existing fire-and-forget Session append is not an acknowledgement boundary because it can return before transcript flush; terminal delivery uses the new synchronous command instead.

**Alternatives considered:**

- Throw and rely on dispatcher dead-lettering: rejected because the operator sees no routed AgentSession outcome and issue-level traceability remains absent.
- Open only a failed AgentSession: rejected because every executable routing hit is modeled as Agent-owned work and must retain an AgentJob outcome.
- Submit a workspace-less job and let the runner reject it: rejected because it preserves the current contract disagreement and consumes runner capacity for invalid server-composed work.
- Open Session before claiming the idempotent AgentJob: rejected because Session merge semantics can accept a later workspace while AgentJob keeps the first input, producing divergent audit and execution state.
- Rely on report/event redelivery without a pending-close reminder: rejected because AgentJob terminal state currently short-circuits report replay and no future activation is guaranteed.
- Rely on routing event redelivery to resume a prepared plan: rejected because redelivery reevaluates mutable rule and Agent state and can skip a plan already claimed by AgentJob.

### 3. Preserve routed lineage on AgentSession metadata

Normal and failed routed launches pass issue and epic lineage from the event into `AgentLaunchContext`, in addition to the existing trigger event and rule labels. `GenericAgentSessionMetadata` already maps these values to persisted labels and the database already has computed columns for issue, trigger event, and trigger rule, so no schema change is required.

The first persisted launch plan is canonical on redelivery. AgentSession is always opened from the plan returned by `EnsurePreparedAsync`, never from the caller's newly resolved values, so a later issue-only lookup cannot overwrite work directory or workspace/issue trigger metadata.

**Alternatives considered:**

- Resolve issue lineage later from the trigger event for every read: rejected because it adds repeated cross-store joins and cannot repair an event that has been retained or deleted differently from the session.
- Add a separate routing-correlation table: rejected because session labels already provide stable indexed correlation and a second write model would duplicate facts.

### 4. Project failure reason and category from one terminal fact

Extend the internal `TerminalFact` projection to parse `failureCategory` alongside its existing `failureReason`, status, completion time, and exit code. `GetGenericSessionSummaryAsync` uses the latest applicable terminal fact for both failure fields and adds `FailureReason` to `GenericAgentSessionSummaryDto`; transcript summary projection remains responsible for model and tool counts. Terminal reduction first applies the existing current-runtime applicability filter, then orders by transcript turn sequence, part sequence, and part id. Part-local sequence alone is insufficient because it restarts in each turn.

Taking both fields from the same terminal fact prevents a current failure reason from being paired with a category left by an older Runtime Session lineage entry. For runner-reported failure, `AgentJobGrain` persists category precedence as output JSON `failureCategory` -> `WorkResult.Error.Code` -> report status. This maps the runner's pre-execution `invalid-input` error correctly; projection alone cannot recover a code discarded at report handling. The API addition is nullable and additive. `RenderAgentSessionShow` prints distinct `failure reason` and `failure category` rows. JSON mode requires no special transformation because it already emits the server payload.

AgentJob failure paths used by this change must all close the generic AgentSession through the shared terminal helper. The new no-dispatch failure command uses that helper; existing dispatch-exhaustion and report-timeout paths already do. Any forced AgentJob failure path covered by generic-session visibility should be converged on the same helper rather than persisting job state alone.

**Alternatives considered:**

- Add `failureReason` to `TranscriptEventSummaryProjector` and keep category from its independent scan: rejected because independently reduced values can come from different terminal attempts.
- Query AgentJob state from the session API: rejected because AgentSession already owns the persisted terminal observation and the read would introduce a reverse lookup from session to work owner.
- Replace `failureCategory` with the reason: rejected because category remains useful for machine grouping while reason is operator-facing evidence.
- Keep deriving category only from output JSON: rejected because runner preflight failures carry category in `WorkResult.Error.Code` and otherwise collapse to generic `failed`.

### 5. Assemble routed failures into the issue feed in AgentOps

Add an issue-scoped assembler under `AgentOps/Services`. It combines:

- Issue events from `IEventStore`;
- WorkflowRun events through an unbounded `WorkflowEventQuerier.ListValidWorkflowEventsAsync` read, preserving Workflow's invalidated-control-event filtering without source-local truncation;
- the single AgentJob-owned failed `session.closed` transcript part for each generic AgentSession matching project, agent-launch issue label, and non-empty trigger event/rule labels. Selection requires payload delivery id and transcript correlation key to equal the stable `agent-job:{jobKey}:terminal` value; Runtime and follow-up closes are excluded.

The assembler projects a failed routed Session into the existing `StoredCloudEventDto` shape as follows:

- `id`: terminal transcript-part id (source-local, not globally unique);
- `eventId`: `{sessionId}:closed:{terminalDeliveryId}`;
- `source`: `AgentSessionEventPersistence.AgentSessionSource(sessionId)`;
- `type`: `session.closed`; `subject`: Session id;
- `time`: terminal part `LastSeenAt`; `specVersion`: `1.0`; `dataContentType`: `application/json`;
- extensions: canonical `projectid` and `issue` lineage;
- data: terminal delivery id, status, exit code, reason, and category plus Session id, Agent id/name, trigger event id, and trigger rule id.

Candidate collection does not truncate any source before global ordering. The assembler loads the complete issue-scoped Issue sequence (`IEventStore.ListIssueEventsAsync` with an unbounded internal limit), all canonical routed Session failures for the issue, and the complete valid WorkflowRun sequence. Workflow exposes an unbounded `ListValidWorkflowEventsAsync` read (or equivalent reusable invalidation filter) that removes invalidated control events but does not take by event id; the existing public limited Workflow read may delegate to it. This is necessary because event timestamps are not specified to be monotonic with source-local ids.

Define one ascending total key `(time, originRank, source ordinal, source-local id, eventId ordinal)`, where origin rank is Issue, WorkflowRun, then AgentSession. Merge the complete candidates, take the greatest `limit` by the exact reverse key, then return that selected set by the ascending key. This resolves non-monotonic timestamps, equal timestamps, and numeric-id collisions across stores without pretending `id` is global. This is a read projection only; it does not append an Issue event or copy Session authority into another aggregate.

`WorkflowEventRoutes` delegates the issue endpoint to this AgentOps assembler. `WorkflowEventQuerier` keeps WorkflowRun-specific selection and invalidation logic but no longer owns cross-domain issue-feed composition. The CLI already prints issue events as returned JSON, so no new CLI event renderer is needed.

Only the canonical AgentJob-owned failure of a routed Session is added by this change. Manual sessions, successful routed sessions, unrelated Runtime/follow-up closes, sessions for another project/issue, and sessions without both trigger labels are excluded.

**Alternatives considered:**

- Query Session rows directly from `WorkflowEventQuerier`: rejected by Workflow's zero-business-context-dependency invariant.
- Append a durable Issue event when AgentSession closes: rejected because it duplicates a Session fact and introduces a cross-aggregate mutation solely for a read view.
- Filter the project-wide activity feed in memory: rejected because unrelated project events can consume the limit before issue filtering, and its current synthetic lifecycle envelope omits trigger correlation.
- Take `limit` candidates from `WorkflowEventQuerier.ListWorkflowEventsAsync`: rejected because that read truncates by source event id, which is not the issue feed's time-first total key and can discard a globally newer event.

### 6. Verify at product and ownership boundaries

Focused coverage will include:

- routing dispatch composition for explicit WorkflowRun workspace, issue-only fallback, and unresolved workspace;
- no runner assignment for a pre-dispatch failure and idempotent replay with one job/session/close fact;
- first-writer behavior when issue-current-workspace resolution changes between deliveries;
- prepared-plan recovery after process loss and subsequent routing-rule/Agent mutation;
- AgentJob grain terminal persistence and close retry behavior;
- generic session summary/API reason-category separation, successful omission, and latest-terminal-fact selection;
- AgentOps issue-feed canonical AgentJob-close selection, unrelated-close exclusion, complete envelope, equal-time ordering, and global newest-N limiting;
- CLI table and JSON output for generic session failures;
- existing runner missing-workspace validation as a defensive contract, manual launch behavior, Inline Agent behavior, and architecture dependency tests.

## Risks / Trade-offs

- `[Risk] An explicit event references an old or missing WorkflowRun while the issue now has a newer run` -> Treat explicit `workflowrunid` as authoritative and fail visibly; never fall forward to unrelated work.
- `[Risk] Issue-only event redelivery resolves a different current workspace` -> Stable event/rule job identity and first-writer semantics preserve the original persisted launch outcome.
- `[Risk] Rule or Agent state changes after AgentJob claims a plan but before Session open` -> AgentJob owns a durable prepared-state reminder and advances only the persisted plan, independent of routing reevaluation.
- `[Risk] AgentJob terminal save succeeds but AgentSession close persistence fails` -> Persist `PendingSessionClose`, keep a durable reminder until synchronous Session acknowledgement, and retry the stable delivery id across activation loss and report replay.
- `[Risk] Persisted workspace identity points to a directory already cleaned on the runner` -> Keep filesystem authority on the runner; the turn may fail with an actionable runtime/workspace reason, but it will no longer fail because dispatch omitted the path.
- `[Risk] New session events alter issue-feed limit results for existing clients` -> Merge before applying the limit, retain chronological ordering and the existing envelope shape, and add only failed routed outcomes required by the spec.
- `[Trade-off] Correct global newest-N selection loads complete issue-scoped source sequences` -> Accept the bounded per-issue read cost for correctness in this change; keep invalidation in Workflow and leave a future database union/keyset optimization behind the same AgentOps contract.
- `[Risk] Historical routed sessions lack issue labels` -> Apply visibility forward only; avoid an unreliable backfill from retained event data.
- `[Trade-off] Workspace resolution adds one Workflow read and sometimes one Issue read per routing hit` -> Perform reads only after a rule is selected, use the explicit WorkflowRun fast path, and keep dry-run free of these reads.

## Migration Plan

1. Add the durable AgentJob pending-close/reminder protocol and synchronous idempotent AgentSession terminal command; migrate every AgentJob terminal path and verify persistence-failure recovery.
2. Add the ownership-validating routing launch-context resolver and AgentJob-owned prepared-launch recovery fence, then switch routed launch to register reminder -> persist plan -> idempotent Session open -> persist launch-ready or terminal transition. Keep the runner workspace requirement unchanged.
3. Add issue/epic metadata to routed launch contexts and the AgentOps issue-feed assembler; switch the issue events API to the assembler while preserving WorkflowRun event filtering.
4. Add `FailureReason` to the generic AgentSession DTO/query and update CLI rendering and contract tests.
5. Deploy the server before or together with the CLI. Older CLIs ignore the additive summary field; the updated CLI against an older server simply has no reason to render.
6. No relational database migration or historical backfill is required. New AgentJob/AgentSession state fields are additive JSON/Orleans serializer fields; existing state and `session.closed` JSON shapes remain readable.

Rollback requires the server to report no outstanding `PendingSessionClose` records and no AgentJob terminal reminders before the old binary is restored; normally each reminder is removed immediately after Session acknowledgement. If pending deliveries cannot drain, deploy a compatibility drain build rather than rolling directly to code that does not implement the reminder. Once drained, rollback is code-only: additive state fields and terminal facts remain readable, and removing the issue-feed projection and summary field requires no data cleanup. The runner requires no deployment or rollback change.

## Open Questions

None. The triggering event's ownership-validated explicit WorkflowRun is authoritative; issue-current-run lookup is used only when explicit lineage is absent and only for a nonterminal bound run. Workspace resolution occurs after envelope-only routing selection, routed launch identity is fenced before Session open, and AgentJob recovery no longer depends on mutable routing redelivery.
