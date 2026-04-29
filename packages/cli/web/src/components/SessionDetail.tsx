import type { CoderSessionItem } from '../lib/types'
import { useSessionTimeline } from '../hooks/useSessionTimeline'
import { RoundSection } from './SessionTimeline'
import { PlanProgressPanel } from './PlanProgressPanel'

interface SessionDetailProps {
  session: CoderSessionItem
  issueNumber: number
  isLive: boolean
}

export function SessionDetail({ session, issueNumber, isLive }: SessionDetailProps) {
  const { rounds, isStreaming, isLoading, planProgress } = useSessionTimeline(issueNumber, session)

  if (isLoading) {
    return (
      <div className="px-3 py-3 border-t border-gray-100">
        <div className="text-xs text-gray-400">Loading...</div>
      </div>
    )
  }

  if (rounds.length === 0 && session.status !== 'running') {
    return (
      <div className="px-3 py-3 border-t border-gray-100">
        <div className="text-xs text-gray-400">No activity recorded</div>
      </div>
    )
  }

  const isSessionRunning = session.status === 'running'

  return (
    <div className="px-3 py-3 space-y-2 border-t border-gray-100 max-h-[400px] overflow-y-auto">
      {planProgress && planProgress.steps.length > 0 && (
        <PlanProgressPanel planProgress={planProgress} />
      )}
      {rounds.map((round) => (
        <RoundSection
          key={`${round.roundIndex}-${round.label}`}
          round={round}
          isLive={isLive && isSessionRunning}
          isStreaming={isStreaming && isSessionRunning}
        />
      ))}
      {rounds.length === 0 && isSessionRunning && (
        <div className="flex items-center gap-2 text-xs text-blue-500">
          <span className="inline-block h-2 w-2 rounded-full bg-blue-500 animate-pulse" />
          Waiting for activity...
        </div>
      )}
    </div>
  )
}
