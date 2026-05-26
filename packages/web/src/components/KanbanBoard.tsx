import { useMemo, useState, useEffect, useCallback } from 'react'
import { Popover, Transition } from '@headlessui/react'
import type { Issue, AgentStatus } from '../lib/types'
import { IssueStage } from '../lib/types'
import { StageColumn } from './StageColumn'
import { IssueCard } from './IssueCard'
import {
  groupIssuesByStage,
  filterClosedFromDone,
  getDoneColumnCounts,
  STAGES,
} from '../lib/kanban-grouping'
import {
  parseBoardQuery,
  deriveBoardColumns,
  serializeBoardQuery,
  type BoardQueryState,
  type SortMode,
} from '../lib/board-query'
import { useLabels } from '../hooks/useQueries'
import { getPriorityStyle } from '../lib/label-colors'
import { deriveAttentionItems } from '../lib/homepage-attention'

interface Props {
  issues: Issue[]
  agentStatus: AgentStatus
  archivedCount?: number
}

const ALL_PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']

function FilterBar({
  state,
  onChange,
  allLabels,
  sort,
  onSortChange,
}: {
  state: BoardQueryState
  onChange: (state: BoardQueryState) => void
  allLabels: string[]
  sort: SortMode
  onSortChange: (s: SortMode) => void
}) {
  const [labelSearch, setLabelSearch] = useState('')
  const [mobileFiltersOpen, setMobileFiltersOpen] = useState(false)

  const filteredLabels = useMemo(() => {
    if (!labelSearch.trim()) return allLabels
    const q = labelSearch.toLowerCase()
    return allLabels.filter((l) => l.toLowerCase().includes(q))
  }, [allLabels, labelSearch])

  const togglePriority = useCallback(
    (p: string) => {
      const has = state.priorities.includes(p)
      const next = has
        ? state.priorities.filter((x) => x !== p)
        : [...state.priorities, p]
      onChange({ ...state, priorities: next })
    },
    [state, onChange],
  )

  const toggleLabel = useCallback(
    (label: string) => {
      const has = state.labels.includes(label)
      const next = has ? state.labels.filter((x) => x !== label) : [...state.labels, label]
      onChange({ ...state, labels: next })
    },
    [state, onChange],
  )

  const activeFilterCount = state.priorities.length + state.labels.length

  const renderPriorityControls = () => (
    <div className="flex items-center gap-1.5">
      <span className="text-xs text-gray-500 font-medium">Priority:</span>
      <div className="flex flex-wrap gap-1">
        {ALL_PRIORITIES.map((p) => {
          const style = getPriorityStyle(p)
          const active = state.priorities.includes(p)
          return (
            <button
              key={p}
              onClick={() => togglePriority(p)}
              className={`rounded-full px-2 py-0.5 text-xs font-medium transition-colors ${
                active ? 'ring-1 ring-offset-1' : 'hover:opacity-80'
              }`}
              style={{
                backgroundColor: style.bg,
                color: style.text,
                ...(active ? { ringColor: style.text } : {}),
              }}
            >
              {p.toUpperCase()}
            </button>
          )
        })}
        {state.priorities.length > 0 && (
          <button
            onClick={() => onChange({ ...state, priorities: [] })}
            className="text-xs text-gray-400 hover:text-gray-600 ml-1"
          >
            Clear
          </button>
        )}
      </div>
    </div>
  )

  const renderLabelControl = () => {
    if (allLabels.length === 0) return null

    return (
      <Popover as="div" className="relative">
        <Popover.Button className="flex items-center gap-1.5 rounded-full px-2 py-0.5 text-xs font-medium bg-gray-100 text-gray-600 hover:bg-gray-200 transition-colors">
          <span className="text-xs text-gray-500 font-medium">Labels:</span>
          {state.labels.length > 0 ? (
            <span className="bg-blue-100 text-blue-700 rounded-full px-1.5 py-0.5">{state.labels.length}</span>
          ) : (
            <span className="text-gray-400">All</span>
          )}
          <svg className="h-3 w-3 text-gray-400" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M5.293 7.293a1 1 0 011.414 0L10 10.586l3.293-3.293a1 1 0 111.414 1.414l-4 4a1 1 0 01-1.414 0l-4-4a1 1 0 010-1.414z" clipRule="evenodd" />
          </svg>
        </Popover.Button>
        <Transition
          enter="transition ease-out duration-100"
          enterFrom="transform opacity-0 scale-95"
          enterTo="transform opacity-100 scale-100"
          leave="transition ease-in duration-75"
          leaveFrom="transform opacity-100 scale-100"
          leaveTo="transform opacity-0 scale-95"
        >
          <Popover.Panel portal={false} className="fixed inset-x-2 top-auto z-50 mt-1 md:absolute md:inset-x-auto md:right-0 md:w-72 origin-top-right rounded-lg bg-white shadow-lg ring-1 ring-black/5 focus:outline-none">
            <div className="p-2 border-b border-gray-100">
              <input
                type="text"
                placeholder="Search labels..."
                className="w-full rounded-md border border-gray-300 px-3 py-1.5 text-xs text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                value={labelSearch}
                onChange={(e) => setLabelSearch(e.target.value)}
              />
            </div>
            <div className="max-h-64 overflow-y-auto p-2">
              {filteredLabels.length === 0 ? (
                <div className="py-4 text-center text-xs text-gray-400">No labels found</div>
              ) : (
                <div className="flex flex-wrap gap-1">
                  {filteredLabels.map((label) => {
                    const active = state.labels.includes(label)
                    return (
                      <button
                        key={label}
                        onClick={() => toggleLabel(label)}
                        className={`rounded-full px-2 py-0.5 text-xs font-medium transition-colors ${
                          active
                            ? 'bg-blue-100 text-blue-700 ring-1 ring-blue-300'
                            : 'bg-gray-100 text-gray-600 hover:bg-gray-200'
                        }`}
                      >
                        {label}
                      </button>
                    )
                  })}
                </div>
              )}
            </div>
            {state.labels.length > 0 && (
              <div className="border-t border-gray-100 p-2">
                <button
                  onClick={() => onChange({ ...state, labels: [] })}
                  className="text-xs text-gray-400 hover:text-gray-600"
                >
                  Clear all labels
                </button>
              </div>
            )}
          </Popover.Panel>
        </Transition>
      </Popover>
    )
  }

  const renderSearchInput = () => (
    <input
      type="text"
      value={state.search}
      onChange={(e) => onChange({ ...state, search: e.target.value })}
      placeholder="Search titles..."
      className="w-full rounded-md border border-gray-300 px-3 py-1.5 text-xs text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
    />
  )

  return (
    <div className="bg-white border-b border-gray-200">
      <div className="hidden md:flex flex-wrap items-center gap-3 px-4 py-2">
        {renderPriorityControls()}
        {renderLabelControl()}
        <div className="flex-1 min-w-[160px] max-w-xs">
          {renderSearchInput()}
        </div>
      </div>

      <div className="md:hidden px-3 py-2 space-y-2">
        <div className="flex items-center gap-2">
          <div className="min-w-0 flex-1">
            {renderSearchInput()}
          </div>
          <button
            type="button"
            data-testid="mobile-filter-toggle"
            onClick={() => setMobileFiltersOpen((open) => !open)}
            className="shrink-0 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-600 hover:bg-gray-50"
          >
            Filters{activeFilterCount > 0 ? ` ${activeFilterCount}` : ''}
          </button>
        </div>

        {mobileFiltersOpen && (
          <div data-testid="mobile-filter-panel" className="space-y-2 rounded-md border border-gray-200 bg-gray-50 p-2">
            <div className="flex flex-wrap items-center gap-2">
              {renderPriorityControls()}
              {renderLabelControl()}
            </div>
            <div className="flex items-center gap-1.5 border-t border-gray-200 pt-2">
              <span className="text-xs text-gray-500 font-medium">Sort:</span>
              <SortSwitcher sort={sort} onChange={onSortChange} />
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

function SortSwitcher({
  sort,
  onChange,
}: {
  sort: SortMode
  onChange: (s: SortMode) => void
}) {
  const options: { value: SortMode; label: string }[] = [
    { value: 'priority', label: 'Priority' },
    { value: 'number', label: '#' },
    { value: 'updated', label: 'Updated' },
  ]

  return (
    <div className="flex items-center gap-0.5">
      {options.map((opt) => (
        <button
          key={opt.value}
          onClick={() => onChange(opt.value)}
          className={`px-2 py-0.5 text-xs rounded transition-colors ${
            sort === opt.value
              ? 'bg-blue-100 text-blue-700 font-medium'
              : 'text-gray-400 hover:text-gray-600 hover:bg-gray-100'
          }`}
        >
          {opt.label}
        </button>
      ))}
    </div>
  )
}

function NeedsAttentionSummary({
  items,
}: {
  items: Array<{ issueNumber: number; issueId: string; label: string; detail?: string }>
}) {
  if (items.length === 0) return null

  return (
    <div className="px-4 py-2 bg-amber-50 border-b border-amber-100">
      <div className="text-xs font-semibold text-amber-700 mb-1.5">Needs attention</div>
      <div className="flex flex-wrap gap-2">
        {items.map((item) => (
          <a
            key={item.issueId}
            href={`/issue/${item.issueNumber}`}
            className="inline-flex items-center gap-1.5 rounded-md bg-white px-2 py-1 text-xs shadow-sm hover:shadow-md transition-shadow border border-amber-200"
          >
            <span className="font-mono text-amber-600">#{item.issueNumber}</span>
            <span className="font-medium text-amber-700">{item.label}</span>
            {item.detail && (
              <span className="text-gray-500 max-w-[200px] truncate">{item.detail}</span>
            )}
          </a>
        ))}
      </div>
    </div>
  )
}

function RunnerUnavailableBanner({ agentStatus }: { agentStatus: AgentStatus }) {
  if (agentStatus.runnerAvailable !== false) return null

  return (
    <div className="px-4 py-2 bg-amber-50 border-b border-amber-100 text-xs text-amber-700">
      {agentStatus.runnerMessage ?? 'No runner is connected. Start a runner before starting workflow work.'}
    </div>
  )
}

function getSearchParams(): string {
  return typeof window !== 'undefined' ? window.location.search : ''
}

export function KanbanBoard({ issues, agentStatus, archivedCount = 0 }: Props) {
  const { data: allLabels = [] } = useLabels()

  const queryState = useMemo(() => parseBoardQuery(getSearchParams()), [])

  const allColumns = useMemo(() => groupIssuesByStage(issues), [issues])

  const [showClosed, setShowClosed] = useState(false)
  const [localState, setLocalState] = useState<BoardQueryState>(queryState)

  useEffect(() => {
    const handler = () => setLocalState(parseBoardQuery(getSearchParams()))
    window.addEventListener('popstate', handler)
    return () => window.removeEventListener('popstate', handler)
  }, [])

  const filteredColumns = useMemo(
    () => deriveBoardColumns(allColumns, localState),
    [allColumns, localState],
  )

  const { closedCount } = useMemo(
    () => getDoneColumnCounts(filteredColumns),
    [filteredColumns],
  )

  const updateState = useCallback((newState: BoardQueryState) => {
    setLocalState(newState)
    const search = serializeBoardQuery(newState)
    const newUrl = search ? `${window.location.pathname}?${search}` : window.location.pathname
    window.history.pushState({}, '', newUrl)
  }, [])

  const displayedColumns = useMemo(
    () => filterClosedFromDone(filteredColumns, showClosed),
    [filteredColumns, showClosed],
  )

  const defaultStage = useMemo(() => {
    const withIssues = displayedColumns.find((c) => c.issues.length > 0)
    return withIssues ? withIssues.key : STAGES[0].key
  }, [displayedColumns])

  const [selectedStage, setSelectedStage] = useState<IssueStage>(defaultStage)

  useEffect(() => {
    setSelectedStage(defaultStage)
  }, [defaultStage])

  const selectedColumn = displayedColumns.find((c) => c.key === selectedStage) ?? displayedColumns[0]

  const attentionItems = useMemo(
    () => deriveAttentionItems(issues, agentStatus),
    [issues, agentStatus],
  )

  return (
    <div className="flex flex-col h-[calc(100vh-4rem)]">
      <RunnerUnavailableBanner agentStatus={agentStatus} />
      <NeedsAttentionSummary items={attentionItems} />
      <FilterBar
        state={localState}
        onChange={updateState}
        allLabels={allLabels}
        sort={localState.sort}
        onSortChange={(s) => updateState({ ...localState, sort: s })}
      />

      <div className="md:hidden flex flex-col flex-1">
        <div className="flex overflow-x-auto snap-x snap-mandatory border-b border-gray-200 bg-white px-2 shrink-0">
          {displayedColumns.map((col) => (
            <button
              key={col.key}
              onClick={() => setSelectedStage(col.key)}
              className={`flex items-center gap-1.5 px-4 py-3 text-sm font-medium whitespace-nowrap snap-start transition-colors min-h-[44px] border-b-2 ${
                col.key === selectedStage
                  ? 'text-blue-600 border-blue-600'
                  : 'text-gray-500 border-transparent hover:text-gray-700'
              }`}
            >
              <span
                className={`inline-block h-2 w-2 rounded-full ${
                  col.key === selectedStage ? 'bg-blue-500' : 'bg-gray-300'
                }`}
              />
              {col.label}
              <span
                className={`text-xs rounded-full px-1.5 py-0.5 ${
                  col.key === selectedStage
                    ? 'bg-blue-50 text-blue-600'
                    : 'bg-gray-100 text-gray-400'
                }`}
              >
                {col.issues.length}
              </span>
            </button>
          ))}
        </div>

        {selectedStage === IssueStage.Cancelled && closedCount > 0 && !showClosed && (
          <div className="px-4 py-2">
            <button
              onClick={() => setShowClosed(true)}
              className="text-xs text-blue-600 hover:text-blue-700 font-medium"
            >
              Show cancelled ({closedCount})
            </button>
          </div>
        )}

        <div className="flex-1 overflow-y-auto p-4 space-y-2">
          {selectedColumn.issues.length === 0 ? (
            <div className="flex items-center justify-center py-12 text-sm text-gray-400">
              No issues in {selectedColumn.label}
            </div>
          ) : (
            selectedColumn.issues.map((issue) => (
              <IssueCard key={issue.id} issue={issue} agentStatus={agentStatus} />
            ))
          )}
        </div>
      </div>

      <div className="hidden md:flex flex-row gap-4 overflow-x-auto p-4 flex-1">
        {displayedColumns.map((col) => (
          <StageColumn
            key={col.key}
            label={col.label}
            issues={col.issues}
            agentStatus={agentStatus}
            isDone={col.key === IssueStage.Done}
            archivedCount={col.key === IssueStage.Done ? archivedCount : undefined}
            sort={localState.sort}
            onSortChange={(s) => updateState({ ...localState, sort: s })}
          />
        ))}
        {closedCount > 0 && !showClosed && (
          <div className="flex items-start pt-2">
            <button
              onClick={() => setShowClosed(true)}
              className="text-xs text-blue-600 hover:text-blue-700 font-medium whitespace-nowrap"
            >
              Show cancelled ({closedCount})
            </button>
          </div>
        )}
      </div>
    </div>
  )
}
