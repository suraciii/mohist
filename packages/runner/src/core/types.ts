export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue }
export type JsonObject = { [key: string]: JsonValue }

export interface WorkDispatchResponse {
  workflowRunId: string
  workId: string
  uses?: string | null
  with?: string | null
  variables?: string | null
  workType: string
  stage?: string | null
  title?: string | null
  projectId?: string | null
  issueId?: string | null
  issueNumber?: number | null
  artifacts?: string | null
  outputs?: string | null
  setVars?: string | null
  ownerKind?: string | null
  agentJobId?: string | null
  cleanupPolicy?: CleanupPolicy | null
}

/**
 * Workspace cleanup policy delivered by the server on every poll.
 * Each nullable field is an explicit unlimited/disabled sentinel — the
 * runner treats `null` as "do not evict by this strategy". The server
 * never scans runner filesystems; this is policy, not actions.
 */
export interface CleanupPolicy {
  retentionDays?: number | null
  storageBudgetBytes?: number | null
  storageTargetWatermarkBytes?: number | null
}

export interface WorkItem {
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
}

export interface RecoveryTaskInput {
  id: string
  title: string
  uses?: string | null
  with?: JsonObject | null
}

export interface WorkItemResult {
  status: string
  message?: string | null
  output?: string | null
  exitCode?: number | null
  artifactUploadIds?: string[] | null
  capturedOutputs?: JsonObject | null
  cleanupAttempts?: number | null
  recoveryTasks?: RecoveryTaskInput[] | null
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
  projectId?: string | null
  issueNumber?: number | null
  acpSessionManager?: import("../runtime/acp-connection.js").AcpSessionManager | null
  acpConnection?: import("../runtime/acp-connection.js").SharedAcpConnection | null
  serverConnection?: import("../server/connection.js").ServerConnection | null
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
  maxConcurrentWorkflows: number
  pollIntervalMs: number
  heartbeatIntervalMs: number
  dispatchLivenessProbeIntervalMs: number
  // Optional override for the convergence backstop cadence (T-003).
  // Defaults to 5 minutes inside RunnerHost. Set to a very large value
  // to effectively disable the periodic tick while keeping startup /
  // reconnect convergence. Used by tests to drive ticks deterministically.
  cleanupConvergenceIntervalMs?: number
}

export interface RunnerRegistration {
  capabilities: string[]
  projectId?: string
  hostname?: string
  coderModels?: string[]
  coderModelVariants?: Record<string, string[]>
  maxWorkflowSlots?: number
  buildGitHash?: string | null
  connectionId?: string | null
}
