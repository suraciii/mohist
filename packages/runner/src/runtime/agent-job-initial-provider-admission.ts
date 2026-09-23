import type { DispatchWorkItem } from '../core/types.js'
import type { ServerConnection } from '../server/connection.js'
import type { BindingResolution } from './agent-job-executor.js'

export async function admitInitialProviderSubmission(
  connection: ServerConnection,
  work: DispatchWorkItem,
  binding: BindingResolution,
  runtime: 'opencode' | 'pi' | 'codex',
  runtimeSessionId: string,
  signal: AbortSignal,
): Promise<void> {
  if (
    !work.agentJobId ||
    !work.initialInputId ||
    !work.initialTurnId ||
    !binding.agentSessionId ||
    !binding.processGeneration ||
    !binding.initialOperationId ||
    !binding.submissionAttemptId
  )
    return
  const body = {
    operationId: binding.initialOperationId,
    submissionAttemptId: binding.submissionAttemptId,
    workId: work.workId,
    processGeneration: binding.processGeneration,
    sessionId: binding.agentSessionId,
    inputId: work.initialInputId,
    turnId: work.initialTurnId,
    runtime,
    runtimeSessionId,
  }
  let receipt
  try {
    receipt = await connection.startAgentJobInitialInput(work.agentJobId, body, signal)
  } catch {
    receipt = await connection.startAgentJobInitialInput(work.agentJobId, body, signal)
  }
  if (!receipt.effectAdmitted || !receipt.submissionAuthorized)
    throw new Error('Initial AgentJob provider submission was not authorized for this executor attempt')
}
