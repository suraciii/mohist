import { useMemo } from 'react'
import { useAgentActivity } from '../../../entities/agent'
import type { AgentActivitySession } from '../../../entities/agent'

export interface UsageSnapshot {
  inputTokens: number
  outputTokens: number
  totalTokens: number
  costAmount: number
  costCurrency: string | null
}

export function computeUsageSnapshot(sessions: AgentActivitySession[]): UsageSnapshot {
  let inputTokens = 0
  let outputTokens = 0
  let totalTokens = 0
  let costAmount = 0
  let costCurrency: string | null = null

  for (const session of sessions) {
    const usage = session.usage
    if (!usage) continue

    inputTokens += usage.inputTokens ?? 0
    outputTokens += usage.outputTokens ?? 0
    totalTokens += usage.totalTokens ?? 0
    costAmount += usage.costAmount ?? 0
    if (costCurrency === null && usage.costCurrency != null) {
      costCurrency = usage.costCurrency
    }
  }

  return { inputTokens, outputTokens, totalTokens, costAmount, costCurrency }
}

export function useActivityUsageSnapshot(): UsageSnapshot {
  const { data } = useAgentActivity()

  return useMemo(() => computeUsageSnapshot(data?.sessions ?? []), [data])
}
