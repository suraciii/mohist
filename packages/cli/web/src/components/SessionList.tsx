import { useState, useEffect, useMemo } from 'react'
import type { CoderSessionItem, Stage } from '../lib/types'
import { useCoderSessions } from '../hooks/useCoderSessions'
import { reconstructRoundsFromLogs } from '../hooks/useSessionTimeline'
import { SessionHeader } from './SessionHeader'
import { RoundSection } from './SessionTimeline'

interface SessionListProps {
  issueNumber: number
  currentStage: Stage | string
  isLive: boolean
}

function SessionDetail({ session, isLive }: { session: CoderSessionItem; isLive: boolean }) {
  const rounds = useMemo(() => reconstructRoundsFromLogs(session.workflowLogs), [session.workflowLogs])

  if (rounds.length === 0) {
    return (
      <div className="px-3 py-3 border-t border-gray-100">
        <div className="text-xs text-gray-400">No activity recorded</div>
      </div>
    )
  }

  return (
    <div className="px-3 py-3 space-y-2 border-t border-gray-100 max-h-[400px] overflow-y-auto">
      {rounds.map((round) => (
        <RoundSection
          key={`${round.roundIndex}-${round.label}`}
          round={round}
          isLive={isLive && session.status === 'running'}
          isStreaming={session.status === 'running'}
        />
      ))}
    </div>
  )
}

export function SessionList({ issueNumber, isLive }: SessionListProps) {
  const { sessions, isLoading } = useCoderSessions(issueNumber)
  const [expandedId, setExpandedId] = useState<string | null>(null)

  useEffect(() => {
    if (sessions.length === 0) return
    const running = sessions.find((s) => s.status === 'running')
    if (running) {
      setExpandedId(running.id)
    }
  }, [sessions])

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

  const handleToggle = (id: string) => {
    setExpandedId((prev) => (prev === id ? null : id))
  }

  return (
    <div className="rounded-lg border border-gray-200 bg-white">
      <div className="px-3 py-2 border-b border-gray-100 flex items-center gap-2">
        <span className="text-sm font-semibold text-gray-700">Sessions</span>
        <span className="text-xs text-gray-400">{sorted.length}</span>
      </div>
      <div className="divide-y divide-gray-100">
        {sorted.map((session) => (
          <div key={session.id} className="rounded-lg">
            <SessionHeader
              session={session}
              isExpanded={expandedId === session.id}
              onClick={() => handleToggle(session.id)}
            />
            {expandedId === session.id && (
              <SessionDetail session={session} isLive={isLive} />
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
