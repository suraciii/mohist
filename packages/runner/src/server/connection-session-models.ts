export interface AgentSessionReconcileBinding {
  readonly sessionId: string
  readonly runtime: 'opencode' | 'pi'
  readonly runtimeSessionId: string
  readonly workDir: string
}

export interface WorkflowAgentSession {
  sessionId: string
  runtimeSessionId?: string | null
  runtime?: string | null
  status?: string | null
  workDir?: string | null
  model?: string | null
  resolvedModel?: string | null
  needsFreshRuntimeSession?: boolean
}

export interface AgentSessionRuntimeEventReceipt {
  type: string
  inputDeliveryId?: string
  agentTurnId?: string
  agentSessionId?: string
}

export interface AgentInputAttachmentContent {
  readonly bytes: Uint8Array
  readonly contentType: string | null
  readonly contentDisposition: string | null
}

export interface AgentSessionRuntimeEventAcceptance {
  id?: string
  type?: string
  sequence?: number
  inputDeliveryId?: string
  agentTurnId?: string
  agentSessionId?: string
}

export interface WorkflowAgentSessionCleanupTurnAcceptance {
  cleanupOperationId: string
  inputDeliveryId: string
  agentTurnId: string
  agentSessionId: string
}

export type AgentSession = WorkflowAgentSession
