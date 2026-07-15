import type { GenericAgentSessionSummaryDto } from '../../../entities/agent'
import type { SessionMetadata } from '../../../entities/coder-session'

function getSessionStatusKind(
  rawStatus: string | undefined,
  lastActivityAt: string | null | undefined,
  isRunning: boolean,
): 'live' | 'stale' | 'completed' | 'failed' | 'probing' | 'finalizing' {
  if (rawStatus === 'failed' || rawStatus === 'timeout' || rawStatus === 'cancelled') {
    return 'failed'
  }
  if (rawStatus === 'completed') return 'completed'
  if (rawStatus === 'inactive') return 'stale'
  if (rawStatus === 'probing') return 'probing'
  if (rawStatus === 'active') return lastActivityAt ? 'live' : 'stale'
  if (!isRunning) return 'completed'
  if (!lastActivityAt) return 'live'
  const lastActivity = new Date(lastActivityAt).getTime()
  const now = Date.now()
  const twoMinutes = 2 * 60 * 1000
  if (now - lastActivity > twoMinutes) return 'stale'
  return 'live'
}

export function buildGenericSessionMetadata(summary: GenericAgentSessionSummaryDto): SessionMetadata {
  const isRunning = summary.status === 'active' || summary.status === 'running' || summary.status === 'probing'
  return {
    sessionId: summary.sessionId,
    sessionName: summary.agentName,
    issueId: '',
    runtimeSessionId: summary.sessionId,
    runtime: null,
    executionId: null,
    title: summary.agentName,
    status: summary.status,
    statusKind: getSessionStatusKind(summary.status, summary.lastActivityAt, isRunning),
    model: summary.resolvedModel,
    stage: null,
    createdAt: summary.createdAt,
    completedAt: null,
    lastActivityAt: summary.lastActivityAt,
    firstPromptSentAt: null,
    lastDataAt: summary.lastActivityAt,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
    partCount: 0,
    toolCount: summary.toolCallCount ?? 0,
    turnCount: 0,
    changedFiles: undefined,
    eventSummary: {
      resolvedModel: summary.resolvedModel,
      failureCategory: summary.failureCategory,
      toolCallCount: summary.toolCallCount,
      toolErrorCount: summary.toolErrorCount,
    },
    usage: summary.usage
      ? {
          inputTokens: summary.usage.inputTokens ?? null,
          outputTokens: summary.usage.outputTokens ?? null,
          totalTokens: summary.usage.totalTokens ?? null,
          cachedReadTokens: summary.usage.cachedReadTokens ?? null,
          thoughtTokens: summary.usage.thoughtTokens ?? null,
          costAmount: summary.usage.costAmount ?? null,
          costCurrency: summary.usage.costCurrency ?? null,
          contextWindowUsed: summary.usage.contextWindowUsed ?? null,
          contextWindowSize: summary.usage.contextWindowSize ?? null,
          contextUsagePercent: summary.usage.contextUsagePercent ?? null,
        }
      : undefined,
  }
}
