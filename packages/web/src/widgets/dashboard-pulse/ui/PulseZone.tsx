import { Link } from 'react-router-dom'
import { useActivityCards } from '@/widgets/coder-session/model/activity-cards'
import { useProjectPath } from '@/entities/project'
import { CompactSessionCard } from './CompactSessionCard'

const MAX_VISIBLE_CARDS = 4

const PILL_STYLE: Record<string, string> = {
  active: 'bg-blue-100 text-blue-700',
  waiting: 'bg-amber-100 text-amber-700',
  completed: 'bg-green-100 text-green-700',
  failed: 'bg-red-100 text-red-700',
}

const PILL_LABEL: Record<string, string> = {
  active: 'Active',
  waiting: 'Waiting',
  completed: 'Completed',
  failed: 'Failed',
}

export function PulseZone() {
  const { activeCards, statusCounts, slotUsage } = useActivityCards()
  const toProjectPath = useProjectPath()
  const visible = activeCards.slice(0, MAX_VISIBLE_CARDS)
  const overflow = activeCards.length - visible.length

  return (
    <div data-testid="pulse-zone" className="flex flex-col gap-3">
      <div
        className="flex flex-wrap items-center gap-x-2 gap-y-1"
        data-testid="pulse-capacity-header"
      >
        <span
          className="ml-auto text-xs text-gray-500 font-medium tabular-nums"
          data-testid="pulse-slots"
        >
          {slotUsage.active}/{slotUsage.max} slots used
        </span>
      </div>

      <div
        className="flex flex-wrap items-center gap-x-2 gap-y-1"
        data-testid="pulse-status-pills"
      >
        {(['active', 'waiting', 'completed', 'failed'] as const).map((key) => (
          <span
            key={key}
            data-testid={`pulse-pill-${key}`}
            className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-semibold ${PILL_STYLE[key]}`}
          >
            <span>{PILL_LABEL[key]}:</span>
            <span className="tabular-nums">{statusCounts[key]}</span>
          </span>
        ))}
      </div>

      {activeCards.length === 0 ? (
        <div
          data-testid="pulse-empty-state"
          className="rounded-md border border-dashed border-gray-200 bg-gray-50 px-3 py-6 text-center"
        >
          <p className="text-xs text-gray-400">No active sessions</p>
        </div>
      ) : (
        <>
          <div className="flex flex-col gap-2" data-testid="pulse-card-list">
            {visible.map((card) => (
              <CompactSessionCard key={card.sessionId} card={card} />
            ))}
          </div>
          {overflow > 0 && (
            <Link
              to={toProjectPath('/activity')}
              data-testid="pulse-overflow-link"
              className="text-xs text-blue-600 hover:text-blue-800 hover:underline self-start"
            >
              +{overflow} more in Activity
            </Link>
          )}
        </>
      )}
    </div>
  )
}
