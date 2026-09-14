import type { AgentExecutionBinding, DispatchReportOwner, DispatchWorkItem, WorkItemResult } from '../core/types.js'

type Fetcher = (input: string, init: RequestInit) => Promise<Response>
type AgentReportBinding = AgentExecutionBinding

export async function reportWork(
  fetcher: Fetcher,
  url: (path: string) => string,
  work: DispatchWorkItem,
  result: WorkItemResult,
  signal: AbortSignal,
  binding?: AgentReportBinding,
  reportOwner?: DispatchReportOwner,
): Promise<{ verdict: 'accepted' | 'refused' | 'outstanding' | null }> {
  const ownerKind = reportOwner?.ownerKind ?? work.ownerKind?.trim().toLowerCase()
  const workflowRunId = reportOwner ? reportOwner.workflowRunId : work.workflowRunId
  const agentJobId = reportOwner ? reportOwner.agentJobId : work.agentJobId
  const body: Record<string, unknown> = {
    workId: work.workId,
    actionAttemptId: work.actionAttemptId ?? null,
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
  if (ownerKind === 'agent-job') {
    if (agentJobId) body.agentJobId = agentJobId
  } else if (ownerKind === 'workflow' && workflowRunId) {
    body.workflowRunId = workflowRunId
  }

  const response = await fetcher(url('report'), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  })
  if (!response.ok) throw new Error(`report failed: ${response.status} ${await response.text()}`)
  try {
    const body = (await response.json()) as { verdict?: unknown }
    return {
      verdict:
        body.verdict === 'accepted' || body.verdict === 'refused' || body.verdict === 'outstanding'
          ? body.verdict
          : null,
    }
  } catch {
    return { verdict: null }
  }
}
