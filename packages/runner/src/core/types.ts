import type { ActionCatalog } from "../actions/manifest.js"

export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue }
export type JsonObject = { [key: string]: JsonValue }

export type WorkType = "task" | "checks"

export interface CheckItem {
  name: string
  title: string
  uses?: string | null
  with?: JsonObject | null
}

export interface TaskArtifactDeclaration {
  path: string
}

/**
 * Mirrors the server domain `WorkItem` (C#) returned by
 * `IWorkflowGrain.PollWorkAsync`. The control plane hands the runner the
 * declaration shape: a task or checks variant with the raw templates
 * (`with`), the declared artifacts, and the declared `setVars`. No
 * resolved variables, no rendered execution context, no loaded
 * prompts. The runner-grain's `WorkflowItemTranslator` owns the
 * WorkItem→WorkDispatch serialization; this TS type is the in-memory
 * counterpart the runner process would hold if it ever needs to speak
 * the domain shape (e.g., for mirroring tests or boundary checks).
 *
 * Runtime execution still consumes the dispatch envelope exposed via
 * `WorkDispatchResponse` — see `poll()` on `ServerConnection`. The two
 * shapes are intentionally separate so the dispatch envelope can carry
 * `variables`, `prompts`, `projectId`, `issueNumber`, etc. without
 * smuggling them into the domain protocol.
 */
export type WorkItem =
  | TaskWorkItem
  | ChecksWorkItem

export interface TaskWorkItem {
  workType: "task"
  stage: string
  id: string
  title?: string | null
  uses?: string | null
  with?: JsonObject | null
  artifacts?: TaskArtifactCapture | null
  setVars?: Record<string, string> | null
  recovery?: JsonObject | null
  recoveryRemaining?: number | null
}

export interface ChecksWorkItem {
  workType: "checks"
  stage: string
  id: string
  items: ReadonlyArray<CheckItem>
}

export interface TaskArtifactCapture {
  files: ReadonlyArray<TaskArtifactDeclaration>
}

/**
 * HTTP envelope returned by the runner poll endpoint. The server's
 * runner-grain serializes a domain `WorkItem` into a `WorkDispatch`
 * carrying the raw `with`/`artifacts` declarations alongside the
 * `variables`/`prompts` snapshot, issue linkage, and the owner
 * identity (`ownerKind` + `agentJobId` for agent-job work,
 * `workflowRunId` for workflow work). The Runner is the single
 * execution-boundary renderer for those raw declarations.
 */
export interface ParentIssueContext {
  title: string
  body: string | null
}

export interface AgentExecutionDefinition {
  instructions: string
  runtime: string
  model?: string | null
  variant?: string | null
  skills: readonly string[]
}

export interface AgentSessionStartup {
  projectId: string
  sessionId: string
  parentSessionId?: string | null
  allowedSubagents: readonly {
    agentId: string
    nameAtLaunch: string
    descriptionAtLaunch: string
  }[]
  spawnCommand: string
  workDir?: string | null
  pinnedRunnerId?: string | null
  agentId?: string | null
  agentName?: string | null
}

export type WorkDispatchResponse = {
  workflowRunId: string
  workId: string
  taskRunId?: string | null
  uses?: string | null
  with?: string | null
  /**
   * Task-level completion contract carried verbatim from the dispatch.
   * The Runner parses and re-renders it against the dispatch snapshot;
   * the Action Input (`with`) MUST NOT contain this. Mirrors
   * `WorkDispatch.Expect` on the server.
   */
  expect?: string | null
  variables?: string | null
  workType: string
  stage?: string | null
  title?: string | null
  projectId?: string | null
  issueNumber?: number | null
  epicNumber?: number | null
  parentIssueContext?: ParentIssueContext | null
  artifacts?: string | null
  outputs?: string | null
  setVars?: string | null
  ownerKind?: string | null
  agentJobId?: string | null
  /**
   * Minted AgentSession id for agent-job dispatches whose launch
   * created a generic (non-workflow) AgentSession. The runner uses
   * this as the session identity for runtime events. Null for
   * workflow dispatches and raw-prompt-only AgentJob validation
   * dispatches. Mirrors `WorkDispatch.AgentSessionId` on the server.
   */
  agentSessionId?: string | null
  recovery?: string | null
  recoveryRemaining?: number | null
  agentDefinition?: AgentExecutionDefinition | null
  agentSessionStartup?: AgentSessionStartup | null
  /**
   * Launch-time `SessionInput` id the coordinator durably recorded
   * on the AgentSession before the AgentJob dispatched. The runner
   * uses this to skip emitting a duplicate `session.input` record
   * for an AgentJob launch (issue-512 T-001). Mirrors
   * `WorkDispatch.InitialInputId` on the server.
   */
  initialInputId?: string | null
  /**
   * Launch-time `AgentTurn` id the coordinator durably recorded on
   * the AgentSession. The runner propagates the id so the Session's
   * turn status can be reconciled with the Job's lifecycle. Mirrors
   * `WorkDispatch.InitialTurnId` on the server.
   */
  initialTurnId?: string | null
}

/**
 * Workspace cleanup policy delivered by the server. Each nullable
 * field is an explicit unlimited/disabled sentinel — the runner treats
 * `null` as "do not evict by this strategy". The server never scans
 * runner filesystems; this is policy, not actions.
 *
 * Sourced via the dedicated config channel
 * `GET /api/runner/{runnerId}/config`; independent of
 * work dispatch.
 */
export interface CleanupPolicy {
  retentionDays?: number | null
  storageBudgetBytes?: number | null
  storageTargetWatermarkBytes?: number | null
}

/**
 * Response body for `GET /api/runner/{runnerId}/config`. The runner
 * fetches this on every cleanup-loop tick and passes the unwrapped
 * `cleanupPolicy` into `cleanupLoop.runOnce`. Wrapper record (rather
 * than bare `CleanupPolicyDto`) leaves room for additional
 * runner-facing config fields to be added additively.
 */
export interface RunnerConfigResponse {
  cleanupPolicy?: CleanupPolicy | null
}

/**
 * The deserialized work envelope consumed by the runner process. The
 * server-side `WorkDispatchResponse` is parsed by the connection layer
 * into this shape: JSON-string fields (`with`, `variables`, `artifacts`,
 * `outputs`, `setVars`) are decoded into structured objects/arrays so
 * the runtime can traverse them without re-parsing. This is the
 * runner's internal "work item" — it is NOT a mirror of the server
 * domain `WorkItem`. The `with` and `expect` fields are raw
 * declarations (template references preserved) and the `variables`
 * field is the dispatch context snapshot the runner renders against.
 */
export interface DispatchWorkItem {
  workflowRunId: string
  workId: string
  taskRunId?: string | null
  workType: string
  stage?: string | null
  title?: string | null
  uses?: string | null
  with?: JsonObject | null
  /**
   * Task-level completion contract, separate from `with`. The
   * executor applies this AFTER the Action returns; the Action
   * never receives or interprets it. Mirrors the `WorkDispatch.Expect`
   * field on the server.
   */
  expect?: JsonObject | null
  variables?: JsonObject | null
  projectId?: string | null
  issueNumber?: number | null
  epicNumber?: number | null
  parentIssueContext?: ParentIssueContext | null
  artifacts?: JsonObject | null
  outputs?: Array<{ name: string; from: string }> | null
  setVars?: Record<string, string> | null
  ownerKind?: string | null
  agentJobId?: string | null
  /**
   * Minted AgentSession id for agent-job dispatches whose launch
   * created a generic (non-workflow) AgentSession. The runner uses
   * this as the session identity for runtime events when the
   * dispatch is owner-kind "agent-job". Null for workflow
   * dispatches. Mirrors `WorkDispatch.AgentSessionId` on the server.
   */
  agentSessionId?: string | null
  recovery?: JsonObject | null
  recoveryRemaining?: number | null
  agentDefinition?: AgentExecutionDefinition | null
  agentSessionStartup?: AgentSessionStartup | null
  /**
   * Launch-time `SessionInput` id the coordinator durably recorded
   * on the AgentSession before the AgentJob dispatched. When set,
   * the runner skips emitting a duplicate `session.input` record
   * for an AgentJob launch (issue-512 T-001). Mirrors
   * `WorkDispatch.InitialInputId` on the server.
   */
  initialInputId?: string | null
  /**
   * Launch-time `AgentTurn` id the coordinator durably recorded on
   * the AgentSession. The runner propagates the id so the Session's
   * turn status can be reconciled with the Job's lifecycle. Mirrors
   * `WorkDispatch.InitialTurnId` on the server.
   */
  initialTurnId?: string | null
}

export interface AddTaskInput {
  id: string
  title: string
  uses?: string | null
  with?: JsonObject | null
  artifacts?: JsonObject | null
  setVars?: Record<string, string> | null
  recovery?: JsonObject | null
  recoveryRemaining?: number | null
  /**
   * Task-level completion contract, separate from `with`. Self-retry
   * copies this alongside the existing fields so completion policy
   * follows the retry attempt.
   */
  expect?: JsonObject | null
}

export interface WorkItemResult {
  status: string
  message?: string | null
  error?: ActionError | null
  /**
   * Successful public output of the dispatched work. Workflow task work
   * carries the Action contract (`JsonObject | null`). Check work carries a
   * structured array of result rows. AgentJob work carries its own
   * non-Action terminal-object shape. The transport is generic JSON so a
   * single envelope covers all three without a discriminator.
   */
  output?: JsonValue | null
  exitCode?: number | null
  artifactUploadIds?: string[] | null
  capturedOutputs?: JsonObject | null
  cleanupAttempts?: number | null
  addTasks?: AddTaskInput[] | null
}

export interface ActionError {
  code: string
  message: string
}

export type ActionResult = (
  | { output: JsonObject | null; error?: never }
  | { output?: never; error: ActionError }
) & {
  effects?: {
    addTasks?: AddTaskInput[]
    writeVars?: JsonObject
  }
  exitCode?: number | null
  /**
   * Runner-private Action-result facts that must never be serialized
   * into `WorkItemResult.output`, `TaskRun.Output`, recovery matching,
   * `setVars` projections, captured outputs, or artifacts. The boundary
   * between `ActionResult` (internal) and `WorkItemResult` (wire) is
   * where the fact is dropped. Only `mohist/opencode`-style agent
   * Actions populate `finalAssistantText`; the executor uses it as the
   * text corpus for `_output` markers.
   */
  turnFact?: { finalAssistantText?: string | null } | null
}

export interface RunnerOptions {
  serverUrl: string
  runnerId: string
  projectId?: string
  runnerRoot: string
  pollIntervalMs: number
  heartbeatIntervalMs: number
  dispatchLivenessProbeIntervalMs: number
  /**
   * The runner's machine credential (Bearer token) issued by the server
   * during install registration. Resolved by the CLI bootstrap from
   * <c>$RUNNER_ROOT/credential</c> or a fresh enrollment-token
   * registration; every server call and the SignalR hub connection
   * present it as <c>Authorization: Bearer</c>.
   */
  credential?: string
  // Optional override for the convergence backstop cadence.
  // Defaults to 5 minutes inside RunnerHost. Set to a very large value
  // to effectively disable the periodic tick while keeping startup /
  // reconnect convergence. Used by tests to drive ticks deterministically.
  cleanupConvergenceIntervalMs?: number

  // Optional override for the cleanup loop cadence.
  // Defaults to 2 minutes inside RunnerHost. The cleanup loop runs
  // retention + budget eviction with pre-delete guards. Set to a very
  // large value to effectively disable the periodic tick. Used by tests
  // to drive ticks deterministically.
  cleanupLoopIntervalMs?: number

  /** Idle grace before an unowned shared Agent runtime is terminated. */
  runtimeIdleGraceMs?: number

  /**
   * Optional override for the incremental task-log flush interval in
   * milliseconds. Defaults to {@link TASK_LOG_FLUSH_INTERVAL_MS}
   * inside RunnerHost. The trigger fires on either an elapsed
   * interval since the last fire or a reached line-count threshold;
   * an empty drain short-circuits without a network round-trip.
   */
  taskLogFlushIntervalMs?: number

  /**
   * Optional override for the incremental task-log flush line-count
   * threshold. Defaults to {@link TASK_LOG_FLUSH_LINE_THRESHOLD}
   * inside RunnerHost. Crossing this on a captured line fires the
   * trigger eagerly so a chatty command is not held back by the
   * interval.
   */
  taskLogFlushLineThreshold?: number

  /**
   * Optional override for the incremental task-log upload timeout in
   * milliseconds. Defaults to
   * {@link TASK_LOG_INCREMENTAL_UPLOAD_TIMEOUT_MS} inside RunnerHost.
   * Distinct from the terminal-batch timeout because incremental
   * batches are smaller but the rail tolerates more slack.
   */
  taskLogIncrementalUploadTimeoutMs?: number

}

export interface RuntimeCatalogEntry {
  models: string[]
  variants: Record<string, string[]>
}

export interface RunnerRegistration {
  capabilities: string[]
  actionCatalog: ActionCatalog
  projectId?: string
  hostname?: string
  coderModels?: string[]
  coderModelVariants?: Record<string, string[]>
  runtimeCatalogs?: Record<string, RuntimeCatalogEntry>
  buildGitHash?: string | null
  component?: string | null
  version?: string | null
  sourceRevision?: string | null
  treeHash?: string | null
  artifactDigest?: string | null
  releaseId?: string | null
  generation?: number | null
  runnerId?: string | null
  connectionId?: string | null
}
