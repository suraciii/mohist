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
  cleanupOperationId?: string
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
  cleanupOperationId?: string
  sequence?: number
  inputDeliveryId?: string
  agentTurnId?: string
  agentSessionId?: string
}

export interface AgentJobInitialRecoveryReceipt {
  phase: string
  candidateCreationAuthorized: boolean
  runtime: string | null
  runtimeSessionId: string | null
}

export interface AgentJobInitialInputReceipt {
  effectAdmitted: boolean
  submissionAuthorized: boolean
  runtime: string | null
  runtimeSessionId: string | null
}

export type AgentSession = WorkflowAgentSession
