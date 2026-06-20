import { useState, useEffect } from 'react'
import { Link } from 'react-router-dom'
import { StatusBar } from '../../../shared/ui/StatusBar'
import { ActiveSessionCard, WaitingCard, RecentCard, UsageSnapshotLabel, useActivityCards, useActivityUsageSnapshot } from '../../../widgets/coder-session'
import { RunnerSummaryBadge } from '../../../widgets/runner-status'
import { useProjectPath } from '../../../entities/project'

function EmptySection({ message }: { message: string }) {
  return (
    <div className="rounded-lg border border-dashed border-gray-200 bg-gray-50 px-4 py-8 text-center">
      <p className="text-sm text-gray-400">{message}</p>
    </div>
  )
}

function SectionHeader({ title, count }: { title: string; count: number }) {
  return (
    <div className="flex items-center gap-2 mb-3">
      <h3 className="text-sm font-semibold text-gray-700">{title}</h3>
      <span className="text-xs text-gray-400">({count})</span>
    </div>
  )
}

export function ActivityPage() {
  const { activeCards, recentCards, waitingCards, statusCounts, slotUsage } = useActivityCards()
  const usageSnapshot = useActivityUsageSnapshot()
  const toProjectPath = useProjectPath()
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(id)
  }, [])

  return (
    <div className="flex-1 flex flex-col min-h-0">
      <StatusBar
        active={statusCounts.active}
        waiting={statusCounts.waiting}
        completed={statusCounts.completed}
        failed={statusCounts.failed}
        activeSlots={slotUsage.active}
        maxSlots={slotUsage.max}
      >
        <RunnerSummaryBadge />
      </StatusBar>

      <div className="flex-1 overflow-y-auto">
        <div className="max-w-3xl mx-auto px-4 py-4 md:px-6 space-y-6">
          <div className="flex justify-end">
            <Link
              to={toProjectPath('/runners')}
              data-testid="activity-runners-link"
              className="inline-flex items-center gap-1 text-xs font-medium text-blue-600 hover:text-blue-700 hover:underline"
            >
              View runners
              <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
                <path fillRule="evenodd" d="M7.293 14.707a1 1 0 010-1.414L10.586 10 7.293 6.707a1 1 0 011.414-1.414l4 4a1 1 0 010 1.414l-4 4a1 1 0 01-1.414 0z" clipRule="evenodd" />
              </svg>
            </Link>
          </div>

          <section>
            <UsageSnapshotLabel snapshot={usageSnapshot} />
          </section>

          <section>
            <SectionHeader title="Active" count={activeCards.length} />
            {activeCards.length === 0 ? (
              <EmptySection message="No active sessions" />
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                {activeCards.map((card) => (
                  <ActiveSessionCard key={card.sessionId} card={card} now={now} />
                ))}
              </div>
            )}
          </section>

          <section>
            <SectionHeader title="Waiting" count={waitingCards.length} />
            {waitingCards.length === 0 ? (
              <EmptySection message="No issues waiting for action" />
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                {waitingCards.map((card) => (
                  <WaitingCard key={card.issueId} card={card} />
                ))}
              </div>
            )}
          </section>

          <section>
            <SectionHeader title="Recent" count={recentCards.length} />
            {recentCards.length === 0 ? (
              <EmptySection message="No recent sessions" />
            ) : (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                {recentCards.map((card) => (
                  <RecentCard key={card.sessionId} card={card} />
                ))}
              </div>
            )}
          </section>
        </div>
      </div>
    </div>
  )
}
