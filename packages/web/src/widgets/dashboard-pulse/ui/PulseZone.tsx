import { Link } from 'react-router-dom'
import { useActivityCards } from '@/widgets/coder-session/model/activity-cards'
import { useProjectPath } from '@/entities/project'
import { CompactSessionCard } from './CompactSessionCard'

const MAX_VISIBLE_CARDS = 4

export function PulseZone() {
  const { activeCards, slotUsage } = useActivityCards()
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
