import { useCoderSessions } from '../hooks/useCoderSessions'
import { SessionHeader } from './SessionHeader'
import { SessionDetail } from './SessionDetail'

interface SessionListProps {
  issueNumber: number
  currentStage: string
  isLive: boolean
}

export function SessionList({ issueNumber }: SessionListProps) {
  const { sessions, isLoading } = useCoderSessions(issueNumber)

  if (isLoading) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <div className="text-sm text-gray-400 text-center">Loading sessions...</div>
      </div>
    )
  }

  if (sessions.length === 0) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <div className="text-sm text-gray-400 text-center">No sessions yet</div>
      </div>
    )
  }

  const sorted = [...sessions].sort(
    (a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
  )

  return (
    <div className="rounded-lg border border-gray-200 bg-white">
      <div className="px-3 py-2 border-b border-gray-100 flex items-center gap-2">
        <span className="text-sm font-semibold text-gray-700">Sessions</span>
        <span className="text-xs text-gray-400">{sorted.length}</span>
      </div>
      <div className="divide-y divide-gray-100">
        {sorted.map((session) => (
          <div key={session.id}>
            <SessionHeader session={session} issueNumber={issueNumber} showTranscriptLink />
            <SessionDetail session={session} />
          </div>
        ))}
      </div>
    </div>
  )
}
