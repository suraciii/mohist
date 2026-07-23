import type { GenericAgentSessionSummaryDto } from '../../../entities/agent'
import { deriveSessionStatusKind } from '../../../entities/coder-session'
import type { SessionMetadata } from '../../../entities/coder-session'

export function buildGenericSessionMetadata(summary: GenericAgentSessionSummaryDto): SessionMetadata {
  return {
    sessionId: summary.sessionId,
    sessionName: summary.agentName,
    runtimeSessionId: summary.runtimeSessionId ?? '',
    runtime: summary.runtime,
    executionId: null,
    title: summary.agentName,
    activity: deriveSessionStatusKind(summary.activity),
    model: summary.resolvedModel,
    stage: null,
    createdAt: summary.createdAt,
    completedAt: null,
    lastActivityAt: summary.lastActivityAt,
    firstPromptSentAt: null,
    lastDataAt: summary.lastActivityAt,
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: summary.failureReason ?? null,
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
