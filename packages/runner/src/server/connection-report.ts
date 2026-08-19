import type { DispatchWorkItem, WorkItemResult } from '../core/types.js'
import type { RuntimeRecoveryReceipt } from '../runtime/recovery-receipt.js'

type Fetcher = (input: string, init: RequestInit) => Promise<Response>
type AgentReportBinding = Pick<
  RuntimeRecoveryReceipt,
  'agentSessionId' | 'agentTurnId' | 'runtime' | 'runtimeSessionId'
>

export async function reportWork(
  fetcher: Fetcher,
  url: (path: string) => string,
  work: DispatchWorkItem,
  result: WorkItemResult,
  signal: AbortSignal,
  binding?: AgentReportBinding,
): Promise<Record<string, unknown>> {
  const ownerKind = work.ownerKind?.trim().toLowerCase()
  const body: Record<string, unknown> = {
    workId: work.workId,
    taskRunId: work.taskRunId ?? null,
    projectId: work.projectId,
    status: result.status,
    message: result.message,
    error: result.error,
    output: result.output,
    exitCode: result.exitCode,
    artifactUploadIds: result.artifactUploadIds ?? null,
    cleanupAttempts: result.cleanupAttempts ?? null,
    addTasks: result.addTasks ?? null,
  }
  if (ownerKind) body.ownerKind = ownerKind
  body.requeue = result.requeue ?? false
  if (binding) {
    body.agentSessionId = binding.agentSessionId
    body.agentTurnId = binding.agentTurnId
    body.runtime = binding.runtime
    body.runtimeSessionId = binding.runtimeSessionId
  }
  if (work.agentJobId) body.agentJobId = work.agentJobId
  if (ownerKind !== 'agent-job') body.workflowRunId = work.workflowRunId

  const response = await fetcher(url('report'), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  })
  if (!response.ok) throw new Error(`report failed: ${response.status} ${await response.text()}`)
  try {
    return (await response.json()) as Record<string, unknown>
  } catch {
    return {}
  }
}
