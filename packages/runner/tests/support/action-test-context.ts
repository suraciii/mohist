import type { JsonObject, ParentIssueContext } from '../../src/core/types.js'
import type { ServerConnection } from '../../src/server/connection.js'
import type { AgentSessionRuntimeEventQueue } from '../../src/server/runtime-event-queue.js'
import type { OpenCodeRuntime } from '../../src/runtime/opencode/index.js'
import type { TaskLogger } from '../../src/runtime/task-log.js'

export interface ActionTestContext {
  workflowRunId: string
  workId: string
  actionAttemptId?: string | null
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
  agentSessionRuntimeEventQueue?: AgentSessionRuntimeEventQueue | null
  runtimeEventRecordId?: () => string
  log?: TaskLogger | null
  writeVars(vars: JsonObject): Promise<void>
}
