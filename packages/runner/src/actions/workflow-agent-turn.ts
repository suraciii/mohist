import { createHash } from "node:crypto"
import type { AgentExecutionObservation, DispatchWorkItem } from "../core/types.js"
import type { ServerConnection, WorkflowAgentSession } from "../server/connection.js"

export interface WorkflowAgentTurnIdentity {
  agentId: string | null
  sessionId: string | null
  inputId: string | null
  turnId: string | null
  operationId: string | null
}

export function workflowAgentTurnIds(work: Pick<DispatchWorkItem, "workflowRunId" | "workId">, sessionName: string) {
  const seed = `${work.workflowRunId}\0${sessionName}\0${work.workId}`
  const digest = createHash("sha256").update(seed).digest("hex").slice(0, 32)
  return {
    inputId: `workflow-input-${digest}`,
    turnId: `workflow-turn-${digest}`,
  }
}

export async function reserveWorkflowAgentTurn(
  connection: ServerConnection,
  work: Pick<DispatchWorkItem, "workflowRunId" | "workId" | "projectId">,
  sessionName: string,
  session: WorkflowAgentSession,
  prompt: string,
  signal: AbortSignal,
): Promise<WorkflowAgentTurnIdentity & { status: string; admissionReady: boolean }> {
  if (!work.projectId) throw new Error("Workflow Agent Action requires a project id to reserve its Session turn")
  const ids = workflowAgentTurnIds(work, sessionName)
  const accepted = await connection.recordWorkflowAgentTurn(work.projectId, work.workflowRunId, sessionName, {
    inputId: ids.inputId,
    turnId: ids.turnId,
    prompt,
    source: "workflow",
  }, signal)
  return {
    agentId: null,
    sessionId: accepted.sessionId || session.sessionId || null,
    inputId: accepted.inputId,
    turnId: accepted.turnId,
    operationId: accepted.operationId ?? null,
    status: accepted.status,
    admissionReady: accepted.admissionReady !== false,
  }
}

export async function abandonWorkflowAgentTurn(
  connection: ServerConnection,
  work: Pick<DispatchWorkItem, "workflowRunId" | "projectId">,
  sessionName: string,
  identity: Pick<WorkflowAgentTurnIdentity, "inputId" | "turnId">,
  signal: AbortSignal,
): Promise<void> {
  if (!work.projectId || !identity.inputId || !identity.turnId) return
  const rollbackSignal = signal.aborted ? new AbortController().signal : signal
  await connection.abandonWorkflowAgentTurn(work.projectId, work.workflowRunId, sessionName, {
    inputId: identity.inputId,
    turnId: identity.turnId,
  }, rollbackSignal)
}

export function agentObservation(
  identity: Partial<WorkflowAgentTurnIdentity> & { agentId?: string | null },
  status: string,
  outcome: string | null,
  reason: string | null,
  nextAction: string | null,
  finalText: string | null,
): AgentExecutionObservation {
  return {
    agentId: identity.agentId ?? null,
    sessionId: identity.sessionId ?? null,
    inputId: identity.inputId ?? null,
    turnId: identity.turnId ?? null,
    status,
    outcome,
    reason,
    nextAction,
    finalText,
  }
}
