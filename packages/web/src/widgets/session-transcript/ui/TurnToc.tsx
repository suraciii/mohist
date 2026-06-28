import type { RefObject } from 'react'
import type { DisplayTurn, PromptKind } from '../model/session-transcript-display'

const KIND_LABELS: Record<PromptKind, string> = {
  initial: 'Initial Task',
  task: 'Task',
  retry: 'Retry',
  followup: 'Follow-up',
  recovery: 'Recovery',
  'legacy-missing': 'Missing Prompt',
}

function deriveEntryLabel(turn: DisplayTurn): string {
  const kindLabel = KIND_LABELS[turn.prompt.kind] ?? turn.prompt.kind
  const title = turn.prompt.title?.slice(0, 60).trim()
  if (!title) return kindLabel
  return `${kindLabel} · ${title}`
}

export interface TurnTocEntry {
  index: number
  label: string
  turnId: string
  target: HTMLElement | null
}

export function buildTurnTocEntries(
  turns: DisplayTurn[],
  turnRefs: Map<number, HTMLDivElement>,
): TurnTocEntry[] {
  return turns.map((turn, i) => {
    const index = i + 1
    return {
      index,
      label: deriveEntryLabel(turn),
      turnId: turn.id,
      target: turnRefs.get(index) ?? null,
    }
  })
}

interface TurnTocListBaseProps {
  entries: TurnTocEntry[]
  onActivate?: (entry: TurnTocEntry) => void
  activeIndex?: number | null
  emptyLabel?: string
}

function scrollEntryIntoView(entry: TurnTocEntry) {
  if (!entry.target) return
  entry.target.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

export function TurnTocList({
  entries,
  onActivate,
  activeIndex = null,
  emptyLabel = 'No turns yet',
}: TurnTocListBaseProps) {
  if (entries.length === 0) {
    return (
      <div data-turn-toc-list="" className="text-xs text-gray-400 px-2 py-2">
        {emptyLabel}
      </div>
    )
  }

  return (
    <ol data-turn-toc-list="" className="space-y-0.5">
      {entries.map((entry) => {
        const isActive = activeIndex === entry.index
        return (
          <li key={entry.turnId}>
            <button
              type="button"
              data-turn-toc-entry=""
              data-turn-toc-entry-index={entry.index}
              aria-current={isActive ? 'true' : undefined}
              onClick={() => {
                if (entry.target) {
                  scrollEntryIntoView(entry)
                }
                onActivate?.(entry)
              }}
              className={
                'w-full text-left rounded px-2 py-1 text-xs transition-colors flex items-start gap-2 ' +
                (isActive
                  ? 'bg-blue-50 text-blue-700'
                  : 'text-gray-600 hover:bg-gray-100')
              }
            >
              <span className="shrink-0 font-mono text-[10px] text-gray-400 mt-0.5 w-5">
                {String(entry.index).padStart(2, '0')}
              </span>
              <span className="truncate min-w-0" title={entry.label}>
                {entry.label}
              </span>
            </button>
          </li>
        )
      })}
    </ol>
  )
}

interface TurnTocRailProps extends TurnTocListBaseProps {
  scrollContainerRef?: RefObject<HTMLElement>
}

export function TurnTocRail(props: TurnTocRailProps) {
  return (
    <aside
      data-turn-toc-rail=""
      className="hidden lg:block w-[180px] shrink-0 sticky top-4 self-start"
    >
      <div className="text-[11px] uppercase tracking-wide text-gray-400 font-medium px-2 mb-2">
        Turns
      </div>
      <nav aria-label="Session transcript table of contents">
        <TurnTocList {...props} />
      </nav>
    </aside>
  )
}