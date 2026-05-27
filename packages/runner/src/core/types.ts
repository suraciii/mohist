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
  session?: AgentSessionContext | null
}

export interface AgentSessionContext {
  id: string
  projectId: string
  issueNumber: number
  workflowRunId: string
  workId: string
  stage?: string | null
  title?: string | null
  externalSessionId?: string | null
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
  session?: AgentSessionContext | null
}

export interface WorkItemResult {
  status: string
  message?: string | null
  output?: string | null
  exitCode?: number | null
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
  session?: AgentSessionContext | null
  telemetry?: RunnerTelemetry
}

export interface RunnerTelemetry {
  started(sessionId: string, body: unknown, signal: AbortSignal): Promise<void>
  events(sessionId: string, events: unknown[], signal: AbortSignal): Promise<void>
  completed(sessionId: string, body: unknown, signal: AbortSignal): Promise<void>
  status?(sessionId: string, body: unknown, signal: AbortSignal): Promise<void>
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
  runnerRoot: string
  pollIntervalMs: number
  heartbeatIntervalMs: number
}

export interface RunnerRegistration {
  capabilities: string[]
  hostname?: string
  coderModels?: string[]
}
