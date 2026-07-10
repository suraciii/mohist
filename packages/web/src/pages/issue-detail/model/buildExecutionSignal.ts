import type { AgentStatus } from '../../../entities/agent'
import type { RuntimeSummary } from '../../../widgets/issue-workflow'
import type { ExecutionSignal } from '../../../widgets/issue-workflow/ui/RuntimeDecisionSurface'

export interface BuildExecutionSignalInput {
  activeSession: { sessionName: string; transcriptPath: string } | null
  agentStatus: Pick<AgentStatus, 'runnerAvailable' | 'runnerMessage' | 'capacity'> | null | undefined
  blocker: unknown
  summary: RuntimeSummary
}

const DEFAULT_RUNNER_UNAVAILABLE_REASON = 'No runner is connected. Start a runner before this issue can run.'

export function buildExecutionSignal(input: BuildExecutionSignalInput): ExecutionSignal | null {
  const { activeSession, agentStatus, blocker, summary } = input

  const runnerGating = pickRunnerGating({ agentStatus, blocker, summary })
  if (!activeSession && !runnerGating) return null

  return {
    activeSession,
    runnerGating,
  }
}

function pickRunnerGating({
  agentStatus,
  blocker,
  summary,
}: {
  agentStatus: BuildExecutionSignalInput['agentStatus']
  blocker: unknown
  summary: RuntimeSummary
}): ExecutionSignal['runnerGating'] {
  if (summary !== 'queued') return null

  if (isDraftBlocker(blocker) || isWaitingForBlocker(blocker)) return null

  if (agentStatus?.runnerAvailable === false) {
    return {
      reason: agentStatus.runnerMessage ?? DEFAULT_RUNNER_UNAVAILABLE_REASON,
      kind: 'runner-unavailable',
    }
  }

  const capacity = agentStatus?.capacity
  if (capacity && capacity.max > 0 && capacity.active >= capacity.max) {
    return {
      reason: `Runner capacity is full (${capacity.active}/${capacity.max}).`,
      kind: 'capacity-full',
    }
  }

  return null
}

function isDraftBlocker(blocker: unknown): boolean {
  return !!blocker && typeof blocker === 'object' && (blocker as { kind?: unknown }).kind === 'draft'
}

function isWaitingForBlocker(blocker: unknown): boolean {
  return !!blocker && typeof blocker === 'object' && (blocker as { kind?: unknown }).kind === 'waiting-for'
}