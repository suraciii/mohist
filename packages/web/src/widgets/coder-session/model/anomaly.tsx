import { useState, useEffect } from 'react'
import type { SessionCard, WaitingCard } from '@/entities/agent-ops'

const THIRTY_SECONDS = 30_000
const THIRTY_MINUTES = 30 * 60_000
const FIVE_MINUTES = 5 * 60_000
const TEN_MINUTES = 10 * 60_000

function useReevaluateTimer(): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), THIRTY_SECONDS)
    return () => clearInterval(id)
  }, [])
  return now
}

export function computeActiveAnomalies(card: SessionCard, now: number): string[] {
  const anomalies: string[] = []
  const createdMs = new Date(card.createdAt).getTime()
  if (now - createdMs > THIRTY_MINUTES) {
    anomalies.push('Running >30min')
  }
  if (card.lastActivityAt) {
    const lastActivityMs = new Date(card.lastActivityAt).getTime()
    if (now - lastActivityMs > FIVE_MINUTES) {
      anomalies.push('No activity >5min')
    }
  }
  return anomalies
}

export function computeWaitingAnomalies(card: WaitingCard, now: number): string[] {
  if (!card.questionAskedAt) return []
  const askedMs = new Date(card.questionAskedAt).getTime()
  if (now - askedMs > TEN_MINUTES) {
    return ['Unanswered >10min']
  }
  return []
}

export function AnomalyBadge({ label }: { label: string }) {
  return (
    <span
      data-testid="anomaly-badge"
      data-tone="warning"
      className="inline-flex items-center gap-0.5 rounded-full border border-warning-border bg-warning-subtle px-1.5 py-0.5 text-[10px] font-semibold text-warning whitespace-nowrap"
    >
      <svg className="w-2.5 h-2.5 shrink-0" viewBox="0 0 16 16" fill="currentColor">
        <path fillRule="evenodd" d="M8.893 1.5c-.183-.31-.52-.5-.887-.5s-.703.19-.886.5L.138 13.499a.98.98 0 0 0 0 1.001c.193.31.53.501.886.501h13.964c.367 0 .704-.19.877-.5a1.03 1.03 0 0 0 .01-1.002L8.893 1.5zm.133 11.497H6.987v-2.003h2.039v2.003zm0-3.004H6.987V5.987h2.039v4.006z" />
      </svg>
      {label}
    </span>
  )
}

export function ActiveSessionAnomalies({ card, now }: { card: SessionCard; now?: number }) {
  const timerNow = useReevaluateTimer()
  const effectiveNow = now ?? timerNow
  const anomalies = computeActiveAnomalies(card, effectiveNow)
  if (anomalies.length === 0) return null
  return (
    <div className="flex flex-wrap gap-1 mt-1">
      {anomalies.map((a) => (
        <AnomalyBadge key={a} label={a} />
      ))}
    </div>
  )
}

export function WaitingSessionAnomalies({ card }: { card: WaitingCard }) {
  const now = useReevaluateTimer()
  const anomalies = computeWaitingAnomalies(card, now)
  if (anomalies.length === 0) return null
  return (
    <div className="flex flex-wrap gap-1 mt-1">
      {anomalies.map((a) => (
        <AnomalyBadge key={a} label={a} />
      ))}
    </div>
  )
}
