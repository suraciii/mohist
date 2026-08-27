import { withTimeout } from './host-timing.js'
import type { InFlightEntry } from './host-state.js'
import type { RunnerHostShutdown } from './host-shutdown-types.js'

export interface ShutdownInFlightEntry {
  work: InFlightEntry['work']
  controller: AbortController
  done: Promise<void>
  shutdown?: InFlightEntry['shutdown']
}

export interface HostShutdownContext {
  readonly inFlight: Map<string, InFlightEntry>
  readonly shutdownStopBudgetMs: number
}

export function createHostShutdown(context: HostShutdownContext): RunnerHostShutdown {
  async function shutdownInFlight(): Promise<void> {
    const entries = [...context.inFlight.values()]
    if (entries.length === 0) return

    const deadline = Date.now() + context.shutdownStopBudgetMs
    for (const entry of entries) {
      entry.shutdown = { requested: true, stopConfirmed: false, operationId: null }
      entry.controller.abort()
    }

    await withTimeout(Promise.allSettled(entries.map((entry) => entry.done)), Math.max(0, deadline - Date.now()))
    for (const entry of entries) {
      context.inFlight.delete(workKey(entry.work))
    }
  }

  return { shutdownInFlight }
}

function workKey(work: InFlightEntry['work']): string {
  const ownerKind = work.ownerKind === 'agent-job' ? 'agent-job' : 'workflow'
  const ownerId = ownerKind === 'agent-job' ? (work.agentJobId ?? '') : work.workflowRunId
  return `${ownerKind}:${ownerId}:${work.workId}`
}

export function positiveBudget(value: number | undefined, fallback: number): number {
  return value !== undefined && Number.isFinite(value) && value >= 0 ? value : fallback
}

export function isSyntheticStopResult(result: { status?: string; error?: { code?: string } | null }): boolean {
  return result.status === 'interrupted' || result.error?.code === 'interrupted'
}

export function isShutdownFailureResult(result: { status?: string }): boolean {
  return result.status === 'failed' || result.status === 'unknown'
}
