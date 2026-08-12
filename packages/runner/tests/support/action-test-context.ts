import type { JsonObject, ParentIssueContext } from "../../src/core/types.js"
import type { ServerConnection } from "../../src/server/connection.js"
import type { AgentSessionRuntimeEventOutbox } from "../../src/server/runtime-event-outbox.js"
import type { OpenCodeRuntime } from "../../src/runtime/opencode/index.js"
import type { TaskLogger } from "../../src/runtime/task-log.js"

export interface ActionTestContext {
  workflowRunId: string
  workId: string
  taskRunId?: string | null
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
  epicNumber?: number | null
  parentIssueContext?: ParentIssueContext | null
  ownerKind?: string | null
  agentJobId?: string | null
  agentSessionId?: string | null
  serverConnection?: ServerConnection | null
  openCodeRuntime?: OpenCodeRuntime | null
  agentSessionRuntimeEventOutbox?: AgentSessionRuntimeEventOutbox | null
  runtimeEventRecordId?: () => string
  log?: TaskLogger | null
  writeVars(vars: JsonObject): Promise<void>
}
