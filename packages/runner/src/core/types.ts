export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue }
export type JsonObject = { [key: string]: JsonValue }

export interface TaskOutputDefinition {
  name: string
  from: string
}

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
  outputs?: TaskOutputDefinition[] | null
}

export interface WorkItemResult {
  status: string
  message?: string | null
  output?: string | null
  exitCode?: number | null
  artifactUploadIds?: string[] | null
  capturedOutputs?: Record<string, JsonValue> | null
  cleanupAttempts?: number | null
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
}

export interface RunnerRegistration {
  capabilities: string[]
  projectId?: string
  hostname?: string
  coderModels?: string[]
  maxWorkflowSlots?: number
  buildGitHash?: string | null
}
