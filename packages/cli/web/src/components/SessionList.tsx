import { useState, useEffect } from 'react'
import type { Stage } from '../lib/types'
import { useCoderSessions } from '../hooks/useCoderSessions'
import { SessionHeader } from './SessionHeader'
import { SessionDetail } from './SessionDetail'

interface SessionListProps {
  issueNumber: number
  currentStage: Stage | string
  isLive: boolean
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
              <SessionDetail session={session} issueNumber={issueNumber} isLive={isLive} />
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
