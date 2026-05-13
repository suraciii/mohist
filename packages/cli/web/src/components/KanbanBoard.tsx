import { useMemo, useState, useEffect, useCallback } from 'react'
import type { Issue, AgentStatus } from '../lib/types'
import { Stage } from '../lib/types'
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
}: {
  state: BoardQueryState
  onChange: (state: BoardQueryState) => void
  allLabels: string[]
}) {
  const [searchValue, setSearchValue] = useState(state.search)

  const handleSearchChange = useCallback(
    (value: string) => {
      setSearchValue(value)
      onChange({ ...state, search: value })
    },
    [state, onChange],
  )

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

  return (
    <div className="flex flex-wrap items-center gap-3 px-4 py-2 bg-white border-b border-gray-200">
      <div className="flex items-center gap-1.5">
        <span className="text-xs text-gray-500 font-medium">Priority:</span>
        <div className="flex gap-1">
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

      {allLabels.length > 0 && (
        <div className="flex items-center gap-1.5">
          <span className="text-xs text-gray-500 font-medium">Labels:</span>
          <div className="flex flex-wrap gap-1">
            {allLabels.slice(0, 8).map((label) => {
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
            {state.labels.length > 0 && (
              <button
                onClick={() => onChange({ ...state, labels: [] })}
                className="text-xs text-gray-400 hover:text-gray-600 ml-1"
              >
                Clear
              </button>
            )}
          </div>
        </div>
      )}

      <div className="flex-1 min-w-[160px] max-w-xs">
        <input
          type="text"
          value={searchValue}
          onChange={(e) => handleSearchChange(e.target.value)}
          placeholder="Search titles..."
          className="w-full rounded-md border border-gray-300 px-3 py-1.5 text-xs text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
        />
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

  const columns = useMemo(
    () => filterClosedFromDone(filteredColumns, false),
    [filteredColumns],
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
    () => filterClosedFromDone(columns, showClosed),
    [columns, showClosed],
  )

  const defaultStage = useMemo(() => {
    const withIssues = displayedColumns.find((c) => c.issues.length > 0)
    return withIssues ? withIssues.key : STAGES[0].key
  }, [displayedColumns])

  const [selectedStage, setSelectedStage] = useState<Stage>(defaultStage)

  useEffect(() => {
    setSelectedStage(defaultStage)
  }, [defaultStage])

  const selectedColumn = displayedColumns.find((c) => c.key === selectedStage) ?? displayedColumns[0]

  return (
    <div className="flex flex-col h-[calc(100vh-4rem)]">
      <FilterBar state={localState} onChange={updateState} allLabels={allLabels} />

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

        <div className="px-2 py-1.5 border-b border-gray-100 bg-gray-50">
          <SortSwitcher
            sort={localState.sort}
            onChange={(s) => updateState({ ...localState, sort: s })}
          />
        </div>

        {selectedStage === Stage.Done && closedCount > 0 && !showClosed && (
          <div className="px-4 py-2">
            <button
              onClick={() => setShowClosed(true)}
              className="text-xs text-blue-600 hover:text-blue-700 font-medium"
            >
              Show closed ({closedCount})
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

      <div className="hidden md:flex flex-col gap-4 overflow-x-auto p-4 flex-1">
        {displayedColumns.map((col) => (
          <StageColumn
            key={col.key}
            label={col.label}
            issues={col.issues}
            agentStatus={agentStatus}
            isDone={col.key === Stage.Done}
            archivedCount={col.key === Stage.Done ? archivedCount : undefined}
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
              Show closed ({closedCount})
            </button>
          </div>
        )}
      </div>
    </div>
  )
}