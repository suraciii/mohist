import { useContext, useEffect, useRef } from 'react'
import { useAgentStatus } from '../../entities/agent'
import { RuntimeToastContext } from '../../shared/ui/toast'

/**
 * Surface a runner-drop notice whenever `useAgentStatus()` transitions to
 * `runnerAvailable === false`. The notice is delivered through the runtime
 * toast host (and via the host's `onNotice` sink into Activity), never as
 * inline issue content.
 *
 * Notice copy / testIds / ttlMs are pinned here bit-for-bit; downstream
 * extraction must not retouch them. See design.md D5 / T-004.
 */
export function useRunnerDropNotice(): void {
  const { data: agentStatus } = useAgentStatus()
  const toastCtx = useContext(RuntimeToastContext)
  const lastSeen = useRef<boolean | null>(null)

  useEffect(() => {
    if (!agentStatus || !toastCtx) return
    const next = agentStatus.runnerAvailable === false
    if (lastSeen.current === null) {
      lastSeen.current = next
      return
    }
    if (next === lastSeen.current) return
    lastSeen.current = next
    if (next) {
      toastCtx.push({
        tone: 'transport',
        title: 'Runner dropped',
        body: agentStatus.runnerMessage ?? 'The workflow runner is no longer reachable. Workflows will resume when it reconnects.',
        testId: 'runtime-toast-runner-dropped',
        ttlMs: 8_000,
      })
    } else {
      toastCtx.push({
        tone: 'transport',
        title: 'Runner reconnected',
        body: 'The workflow runner is back online.',
        testId: 'runtime-toast-runner-reconnected',
        ttlMs: 5_000,
      })
    }
  }, [agentStatus, toastCtx])
}