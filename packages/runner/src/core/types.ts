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
 * declaration shape: a task or checks variant with the unrendered
 * templates (`with`), the declared artifacts, and the declared `setVars`.
 * No resolved variables, no rendered execution context, no loaded
 * prompts. The runner-grain's `WorkflowItemTranslator` owns the
 * WorkItem→WorkDispatch rendering; this TS type is the in-memory
 * counterpart the runner process would hold if it ever needs to speak
 * the domain shape (e.g., for mirroring tests or boundary checks).
 *
 * Runtime execution still consumes the rendered envelope exposed via
 * `WorkDispatchResponse` — see `poll()` on `ServerConnection`. The two
 * shapes are intentionally separate so the rendered envelope can carry
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
 * runner-grain renders a domain `WorkItem` into
 * a `WorkDispatch` and serializes the rendered envelope here so the
 * runner process can execute without re-rendering. The fields include
 * the pre-rendered `with`/`artifacts`, `variables`/`prompts`, issue
 * linkage, and the owner identity (`ownerKind` + `agentJobId` for
 * agent-job work, `workflowRunId` for workflow work).
 */
export type WorkDispatchResponse = {
  workflowRunId: string
  workId: string
  uses?: string | null
  with?: string | null
  variables?: string | null
  workType: string
  stage?: string | null
  title?: string | null
  projectId?: string | null
  issueNumber?: number | null
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
}

/**
 * Workspace cleanup policy delivered by the server. Each nullable
 * field is an explicit unlimited/disabled sentinel — the runner treats
 * `null` as "do not evict by this strategy". The server never scans
 * runner filesystems; this is policy, not actions.
 *
 * Sourced via the dedicated config channel
 * `GET /api/runner/{runnerId}/config` (issue-359); independent of
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
 * domain `WorkItem`. The runner process only
 * ever sees this rendered envelope; the unrendered domain shape lives
 * on the server.
 */
export interface RenderedWorkItem {
  workflowRunId: string
  workId: string
  workType: string
  stage?: string | null
  title?: string | null
  uses?: string | null
  with?: JsonObject | null
  variables?: JsonObject | null
  projectId?: string | null
  issueNumber?: number | null
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
}

export interface WorkItemResult {
  status: string
  message?: string | null
  output?: string | null
  exitCode?: number | null
  artifactUploadIds?: string[] | null
  capturedOutputs?: JsonObject | null
  cleanupAttempts?: number | null
  addTasks?: AddTaskInput[] | null
}

export interface ActionContext {
  workflowRunId: string
  workId: string
  workType: string
  stage?: string | null
  title?: string | null
  uses?: string | null
  with?: JsonObject | null
  variables: JsonObject
  workDir: string
  signal: AbortSignal
  recovery?: JsonObject | null
  projectId?: string | null
  issueNumber?: number | null
  ownerKind?: string | null
  agentJobId?: string | null
  agentSessionId?: string | null
  acpSessionManager?: import("../runtime/acp-connection.js").AcpSessionManager | null
  acpConnection?: import("../runtime/acp-connection.js").SharedAcpConnection | null
  serverConnection?: import("../server/connection.js").ServerConnection | null
  /**
   * Single sink for ops command output. Every ops output (workspace
   * prep, branch stability, action body, cleanup) flows through
   * `log.write(source, text)` so masking, monotonic `seq` assignment,
   * and buffering happen in exactly one place. Exposed as a value
   * rather than a factory because the executor thread constructs one
   * `TaskLogger` per work item and reuses it across all phases; the
   * logger is intentionally missing on contexts that do not capture
   * (agent-only paths) so wiring stays opt-in.
   */
  log?: import("../runtime/task-log.js").TaskLogger | null
  /**
   * Persist workflow runtime variables immediately, before the task completes.
   * This is distinct from declarative `setVars`, which only patches variables
   * after a task succeeds. Mid-execution writes are best-effort and are NOT
   * rolled back if the task later fails, so retries can observe the value.
   */
  writeVars(vars: JsonObject): Promise<void>
}

export interface ActionResult {
  status: string
  message?: string | null
  output?: string | null
  exitCode?: number | null
}

export interface RunnerOptions {
  serverUrl: string
  runnerId: string
  projectId?: string
  runnerRoot: string
  pollIntervalMs: number
  heartbeatIntervalMs: number
  dispatchLivenessProbeIntervalMs: number
  // Optional override for the convergence backstop cadence (T-003).
  // Defaults to 5 minutes inside RunnerHost. Set to a very large value
  // to effectively disable the periodic tick while keeping startup /
  // reconnect convergence. Used by tests to drive ticks deterministically.
  cleanupConvergenceIntervalMs?: number

  // Optional override for the cleanup loop cadence (T-004).
  // Defaults to 2 minutes inside RunnerHost. The cleanup loop runs
  // retention + budget eviction with pre-delete guards. Set to a very
  // large value to effectively disable the periodic tick. Used by tests
  // to drive ticks deterministically.
  cleanupLoopIntervalMs?: number

  /**
   * Optional override for the incremental task-log flush interval in
   * milliseconds. Defaults to {@link TASK_LOG_FLUSH_INTERVAL_MS}
   * inside RunnerHost. The trigger fires on either an elapsed
   * interval since the last fire or a reached line-count threshold;
   * an empty drain short-circuits without a network round-trip
   * (design D1).
   */
  taskLogFlushIntervalMs?: number

  /**
   * Optional override for the incremental task-log flush line-count
   * threshold. Defaults to {@link TASK_LOG_FLUSH_LINE_THRESHOLD}
   * inside RunnerHost. Crossing this on a captured line fires the
   * trigger eagerly so a chatty command is not held back by the
   * interval (design D1).
   */
  taskLogFlushLineThreshold?: number

  /**
   * Optional override for the incremental task-log upload timeout in
   * milliseconds. Defaults to
   * {@link TASK_LOG_INCREMENTAL_UPLOAD_TIMEOUT_MS} inside RunnerHost.
   * Distinct from the terminal-batch timeout because incremental
   * batches are smaller but the rail tolerates more slack (design D1).
   */
  taskLogIncrementalUploadTimeoutMs?: number
}

export interface RunnerRegistration {
  capabilities: string[]
  projectId?: string
  hostname?: string
  coderModels?: string[]
  coderModelVariants?: Record<string, string[]>
  buildGitHash?: string | null
  connectionId?: string | null
}
