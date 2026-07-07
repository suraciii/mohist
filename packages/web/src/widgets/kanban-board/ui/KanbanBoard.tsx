import { useMemo, useState, useEffect, useCallback } from 'react'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Link } from 'react-router-dom'
import { AlertTriangleIcon, SearchIcon, XIcon } from 'lucide-react'
import type { AgentStatus } from '../../../entities/agent'
import { IssueStatus, deriveAttentionItems, type Issue } from '../../../entities/issue'
import { useRunnerSummary } from '../../../entities/runner/api/queries'
import { StageColumn } from './StageColumn'
import { IssueCard } from './IssueCard'
import {
  groupIssuesByStage,
  filterCancelledFromColumns,
  getCancelledColumnCount,
  STAGES,
} from '../model/kanban-grouping'
import {
  parseBoardQuery,
  deriveBoardColumns,
  serializeBoardQuery,
  type BoardQueryState,
  type SortMode,
} from '../model/board-query'
import { deriveLabelPairsFromIssues, formatLabelToken } from '../../../entities/issue/model/labels'
import { useProjectPath } from '../../../entities/project'
import { getPriorityStyle } from '../../../shared/lib/label-colors'
import { getStageColors } from '../model/stage-colors'

interface Props {
  issues: Issue[]
  agentStatus: AgentStatus
  archivedCount?: number
}

const ALL_PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']
const SORT_OPTIONS: { value: SortMode; label: string }[] = [
  { value: 'priority', label: 'Priority' },
  { value: 'number', label: '#' },
  { value: 'updated', label: 'Updated' },
]

function PriorityChips({
  active,
  onToggle,
  onClear,
  showLabel = false,
}: {
  active: string[]
  onToggle: (p: string) => void
  onClear: () => void
  showLabel?: boolean
}) {
  return (
    <div className="flex items-center gap-1">
      {showLabel && (
        <span className="text-[11px] text-muted-foreground font-medium mr-1">
          Priority:
        </span>
      )}
      {ALL_PRIORITIES.map((p) => {
        const style = getPriorityStyle(p)
        const isActive = active.includes(p)
        return (
          <Button
            key={p}
            variant="ghost"
            size="xs"
            onClick={() => onToggle(p)}
            data-testid={`priority-chip-${p}`}
            data-active={isActive}
            className={`h-6 rounded-full border px-2 text-[11px] font-semibold ${style.className} ${
              isActive ? 'ring-2 ring-offset-1 ring-foreground/40' : 'hover:opacity-80'
            }`}
          >
            {p.toUpperCase()}
          </Button>
        )
      })}
      {active.length > 0 && (
        <Button
          variant="link"
          size="xs"
          onClick={onClear}
          className="h-auto p-0 ml-1 text-muted-foreground/70 hover:text-muted-foreground"
        >
          Clear
        </Button>
      )}
    </div>
  )
}

function LabelTrigger({
  selectedCount,
  onClick,
}: {
  selectedCount: number
  onClick: () => void
}) {
  if (selectedCount > 0) {
    return (
      <Button
        variant="ghost"
        size="sm"
        onClick={onClick}
        data-testid="label-chip"
        data-active={true}
        className="h-7 rounded-full px-2.5 text-xs font-medium bg-blue-100 text-blue-700 hover:bg-blue-100"
      >
        <span>Labels:</span>
        <span className="ml-1 rounded-full bg-blue-600 text-white px-1.5 text-[10px]">
          {selectedCount}
        </span>
      </Button>
    )
  }
  return (
    <Button
      variant="ghost"
      size="sm"
      onClick={onClick}
      data-testid="label-chip"
      className="h-7 rounded-full px-2.5 text-xs font-medium bg-muted text-muted-foreground hover:bg-muted/80"
    >
      <span>Labels:</span>
    </Button>
  )
}

function SortToggle({
  sort,
  onChange,
  showLabel = false,
}: {
  sort: SortMode
  onChange: (s: SortMode) => void
  showLabel?: boolean
}) {
  return (
    <div className="flex items-center gap-0.5 ml-auto">
      {showLabel && (
        <span className="text-[11px] text-muted-foreground/70 font-medium mr-1">
          Sort:
        </span>
      )}
      {!showLabel && (
        <span className="text-[11px] text-muted-foreground/70 font-medium mr-1">
          Sort
        </span>
      )}
      {SORT_OPTIONS.map((opt) => (
        <Button
          key={opt.value}
          variant="ghost"
          size="xs"
          onClick={() => onChange(opt.value)}
          data-testid={`sort-${opt.value}`}
          data-active={sort === opt.value}
          className={`h-6 rounded px-2 text-[11px] ${
            sort === opt.value
              ? 'bg-blue-100 text-blue-700 font-medium hover:bg-blue-100'
              : 'text-muted-foreground/70 hover:text-foreground hover:bg-muted'
          }`}
        >
          {opt.label}
        </Button>
      ))}
    </div>
  )
}

function FilterBar({
  state,
  onChange,
  allLabels,
  sort,
  onSortChange,
}: {
  state: BoardQueryState
  onChange: (state: BoardQueryState) => void
  allLabels: Array<{ key: string; value: string }>
  sort: SortMode
  onSortChange: (s: SortMode) => void
}) {
  const [labelSearch, setLabelSearch] = useState('')
  const [mobileFiltersOpen, setMobileFiltersOpen] = useState(false)

  const filteredLabels = useMemo(() => {
    if (!labelSearch.trim()) return allLabels
    const q = labelSearch.trim().toLowerCase()
    return allLabels.filter((pair) => {
      const token = formatLabelToken(pair.key, pair.value).toLowerCase()
      return token.includes(q)
    })
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
    (pair: { key: string; value: string }) => {
      const token = formatLabelToken(pair.key, pair.value)
      const has = state.labels.includes(token)
      const next = has ? state.labels.filter((x) => x !== token) : [...state.labels, token]
      onChange({ ...state, labels: next })
    },
    [state, onChange],
  )

  const hasActiveFilters = state.priorities.length > 0 || state.labels.length > 0
  const activeFilterCount = state.priorities.length + state.labels.length

  const labelPopover = (
    <Popover>
      <PopoverTrigger
        render={
          <LabelTrigger
            selectedCount={state.labels.length}
            onClick={() => undefined}
          />
        }
      />
      <PopoverContent className="origin-top-right w-72 p-0" align="start">
        <div className="p-2 border-b">
          <Input
            type="text"
            placeholder="Search labels..."
            className="text-xs h-7"
            value={labelSearch}
            onChange={(e) => setLabelSearch(e.target.value)}
            data-testid="label-search"
          />
        </div>
        <div className="max-h-64 overflow-y-auto p-2">
          {filteredLabels.length === 0 ? (
            <div className="py-4 text-center text-xs text-muted-foreground/70">
              No labels found
            </div>
          ) : (
            <div className="flex flex-wrap gap-1">
              {filteredLabels.map((pair) => {
                const token = formatLabelToken(pair.key, pair.value)
                const active = state.labels.includes(token)
                return (
                  <Button
                    key={token}
                    variant="ghost"
                    size="xs"
                    onClick={() => toggleLabel(pair)}
                    data-testid={`label-option-${token}`}
                    data-active={active}
                    className={`rounded-full ${
                      active
                        ? 'bg-blue-100 text-blue-700 ring-1 ring-blue-300'
                        : 'bg-muted text-muted-foreground hover:bg-muted/80'
                    }`}
                  >
                    {token}
                  </Button>
                )
              })}
            </div>
          )}
        </div>
        {state.labels.length > 0 && (
          <div className="border-t p-2 flex justify-end">
            <Button
              variant="link"
              size="xs"
              onClick={() => onChange({ ...state, labels: [] })}
              className="h-auto p-0 text-muted-foreground/70 hover:text-muted-foreground"
            >
              Clear all labels
            </Button>
          </div>
        )}
      </PopoverContent>
    </Popover>
  )

  return (
    <div className="bg-background border-b">
      {/* Desktop: single compact row */}
      <div className="hidden md:flex flex-wrap items-center gap-2 px-4 py-2">
        <PriorityChips
          active={state.priorities}
          onToggle={togglePriority}
          onClear={() => onChange({ ...state, priorities: [] })}
        />
        {allLabels.length > 0 && labelPopover}
        <div className="relative flex-1 min-w-[180px] max-w-xs">
          <SearchIcon className="absolute left-2 top-1/2 -translate-y-1/2 size-3.5 text-muted-foreground/60" />
          <Input
            type="text"
            value={state.search}
            onChange={(e) => onChange({ ...state, search: e.target.value })}
            placeholder="Search titles..."
            data-testid="search-input"
            className="h-7 text-xs pl-7 pr-7"
          />
          {state.search && (
            <button
              type="button"
              onClick={() => onChange({ ...state, search: '' })}
              className="absolute right-1.5 top-1/2 -translate-y-1/2 p-0.5 rounded text-muted-foreground/60 hover:text-foreground"
              aria-label="Clear search"
            >
              <XIcon className="size-3" />
            </button>
          )}
        </div>
        <SortToggle sort={sort} onChange={onSortChange} />
      </div>

      {/* Mobile: search + filter toggle */}
      <div className="md:hidden px-3 py-2 space-y-2">
        <div className="flex items-center gap-2">
          <div className="relative min-w-0 flex-1">
            <SearchIcon className="absolute left-2 top-1/2 -translate-y-1/2 size-3.5 text-muted-foreground/60" />
            <Input
              type="text"
              value={state.search}
              onChange={(e) => onChange({ ...state, search: e.target.value })}
              placeholder="Search titles..."
              data-testid="search-input"
              className="h-8 text-xs pl-7"
            />
          </div>
          <Button
            variant="outline"
            size="sm"
            data-testid="mobile-filter-toggle"
            onClick={() => setMobileFiltersOpen((open) => !open)}
            className="h-8"
          >
            Filters{activeFilterCount > 0 ? ` ${activeFilterCount}` : ''}
          </Button>
        </div>

        {mobileFiltersOpen && (
          <div
            data-testid="mobile-filter-panel"
            className="space-y-2 rounded-md border bg-muted p-2"
          >
            <div className="flex flex-wrap items-center gap-1.5">
              <PriorityChips
                active={state.priorities}
                onToggle={togglePriority}
                onClear={() => onChange({ ...state, priorities: [] })}
                showLabel
              />
            </div>
            <div className="flex flex-wrap items-center gap-1.5">
              {labelPopover}
              {hasActiveFilters && (
                <Button
                  variant="link"
                  size="xs"
                  onClick={() =>
                    onChange({ ...state, priorities: [], labels: [] })
                  }
                  className="h-auto p-0 text-muted-foreground/70"
                >
                  Clear filters
                </Button>
              )}
            </div>
            <div className="flex items-center gap-1 border-t pt-2">
              <SortToggle sort={sort} onChange={onSortChange} showLabel />
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

function NeedsAttentionSummary({
  items,
}: {
  items: Array<{ issueNumber: number; issueId: string; label: string; detail?: string }>
}) {
  const toProjectPath = useProjectPath()

  if (items.length === 0) return null

  return (
    <div
      data-testid="needs-attention-summary"
      className="relative bg-amber-50 bg-gradient-to-r from-amber-50 to-amber-50/40 border-b border-amber-200"
    >
      <div className="absolute left-0 top-0 bottom-0 w-1 bg-amber-500" />
      <div className="px-4 sm:px-6 py-2.5 flex items-start gap-3">
        <div className="flex items-center gap-1.5 pt-0.5">
          <span className="inline-flex items-center justify-center size-5 rounded-full bg-amber-500 text-white">
            <AlertTriangleIcon className="size-3" />
          </span>
          <span className="text-xs font-semibold text-amber-800 uppercase tracking-wide">
            Needs attention
          </span>
          <span className="text-xs text-amber-700/80 font-medium">
            ({items.length})
          </span>
        </div>
        <div className="flex-1 flex flex-wrap gap-1.5 min-w-0">
          {items.slice(0, 6).map((item) => (
            <a
              key={item.issueId}
              href={toProjectPath(`/issues/${item.issueNumber}`)}
              data-testid={`attention-link-${item.issueNumber}`}
              className="inline-flex items-center gap-1.5 rounded-md bg-white px-2 py-1 text-xs shadow-sm hover:shadow border border-amber-200 transition-shadow"
            >
              <span className="font-mono font-semibold text-amber-700">
                #{item.issueNumber}
              </span>
              <span className="font-medium text-amber-900">{item.label}</span>
              {item.detail && (
                <span className="text-muted-foreground max-w-[160px] truncate hidden sm:inline">
                  {item.detail}
                </span>
              )}
            </a>
          ))}
          {items.length > 6 && (
            <span className="text-xs text-amber-700 self-center font-medium">
              +{items.length - 6} more
            </span>
          )}
        </div>
      </div>
    </div>
  )
}

function RunnerUnavailableBanner({
  agentStatus,
}: {
  agentStatus: AgentStatus
}) {
  const { hasConnectedCapacity } = useRunnerSummary()
  const toProjectPath = useProjectPath()
  if (hasConnectedCapacity) return null

  return (
    <div className="px-4 py-2 bg-amber-50 border-b border-amber-100 text-xs text-amber-700">
      {agentStatus.runnerMessage ?? 'No runner is connected.'}{' '}
      <Link to={toProjectPath('/activity')} className="underline hover:no-underline">
        View runner status
      </Link>{' '}
      or start a runner before starting workflow work.
    </div>
  )
}

function getSearchParams(): string {
  return typeof window !== 'undefined' ? window.location.search : ''
}

export function KanbanBoard({ issues, agentStatus, archivedCount = 0 }: Props) {
  const allLabels = useMemo(() => deriveLabelPairsFromIssues(issues), [issues])

  const queryState = useMemo(() => parseBoardQuery(getSearchParams()), [])

  const allColumns = useMemo(() => groupIssuesByStage(issues), [issues])

  const [showCancelled, setShowCancelled] = useState(false)
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

  // Mobile tab badge counts and the in-list Show/Hide cancelled link read
  // from `allColumns` (pre-filter, pre-toggle). The badge answers "how many
  // cancelled issues exist?", not "how many are visible right now?", so it
  // must stay stable when the user toggles `showCancelled` or applies a
  // board query filter. The list body below still iterates `displayedColumns`
  // so the toggle actually hides the cards in the mobile list.
  const { cancelledCount } = useMemo(
    () => getCancelledColumnCount(allColumns),
    [allColumns],
  )

  const updateState = useCallback((newState: BoardQueryState) => {
    setLocalState(newState)
    const search = serializeBoardQuery(newState)
    const newUrl = search ? `${window.location.pathname}?${search}` : window.location.pathname
    window.history.pushState({}, '', newUrl)
  }, [])

  const displayedColumns = useMemo(
    () => filterCancelledFromColumns(filteredColumns, showCancelled),
    [filteredColumns, showCancelled],
  )

  const defaultStage = useMemo(() => {
    const withIssues = displayedColumns.find((c) => c.issues.length > 0)
    return withIssues ? withIssues.key : STAGES[0].key
  }, [displayedColumns])

  const [selectedStage, setSelectedStage] = useState<IssueStatus>(defaultStage)

  useEffect(() => {
    setSelectedStage(defaultStage)
  }, [defaultStage])

  const selectedColumn = displayedColumns.find((c) => c.key === selectedStage) ?? displayedColumns[0]

  // The mobile list body must respect `showCancelled` so the in-list toggle
  // actually hides the cards. `displayedColumns` is now an identity seam
  // (T-002), so the Cancelled column still carries its full issue set —
  // we drop the body content here at the render seam only.
  const visibleSelectedColumn = useMemo(() => {
    if (selectedStage === IssueStatus.Cancelled && !showCancelled) {
      return { ...selectedColumn, issues: [] }
    }
    return selectedColumn
  }, [selectedColumn, selectedStage, showCancelled])

  const attentionItems = useMemo(
    () => deriveAttentionItems(issues, agentStatus),
    [issues, agentStatus],
  )

  return (
    <div data-testid="kanban-board-root" className="flex flex-col min-w-0 h-[calc(100vh-3rem)]">
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
        <div className="flex overflow-x-auto snap-x snap-mandatory border-b bg-background px-2 shrink-0">
          {allColumns.map((col) => {
            const colors = getStageColors(col.key)
            const active = col.key === selectedStage
            return (
              <Button
                key={col.key}
                variant="ghost"
                onClick={() => setSelectedStage(col.key)}
                data-testid={`mobile-stage-tab-${col.key}`}
                data-active={active}
                className={`flex items-center gap-1.5 px-4 py-3 text-sm font-medium whitespace-nowrap snap-start transition-colors min-h-[44px] border-b-2 rounded-none ${
                  active
                    ? `${colors.labelClass} ${colors.bottomBorder}`
                    : 'text-muted-foreground border-transparent hover:text-foreground/80'
                }`}
              >
                <span
                  className={`inline-block h-2 w-2 rounded-full ${active ? colors.accent : 'bg-muted-foreground/40'}`}
                />
                {col.label}
                <span
                  className={`text-xs rounded-full px-1.5 py-0.5 ${
                    active ? 'bg-muted text-foreground/80' : 'bg-muted text-muted-foreground/70'
                  }`}
                >
                  {col.issues.length}
                </span>
              </Button>
            )
          })}
        </div>

        {selectedStage === IssueStatus.Cancelled && cancelledCount > 0 && (
          <div className="px-4 py-2">
            <Button
              variant="link"
              size="xs"
              data-testid="mobile-cancelled-toggle"
              onClick={() => setShowCancelled((value) => !value)}
            >
              {showCancelled
                ? 'Hide cancelled'
                : `Show cancelled (${cancelledCount})`}
            </Button>
          </div>
        )}

        <div className="flex-1 overflow-y-auto p-4 space-y-2">
          {visibleSelectedColumn.issues.length === 0 ? (
            <div className="flex items-center justify-center py-12 text-sm text-muted-foreground/70">
              No issues in {visibleSelectedColumn.label}
            </div>
          ) : (
            visibleSelectedColumn.issues.map((issue) => (
              <IssueCard key={issue.id} issue={issue} agentStatus={agentStatus} />
            ))
          )}
        </div>
      </div>

      <div data-testid="kanban-board-row" className="hidden md:flex flex-row gap-4 overflow-x-auto p-4 flex-1 min-w-0">
        {displayedColumns.map((col) => {
          const isCancelled = col.key === IssueStatus.Cancelled
          const cancelledColumnHidden = isCancelled && !showCancelled && col.issues.length > 0
          return (
            <StageColumn
              key={col.key}
              label={col.label}
              status={col.key}
              issues={col.issues}
              agentStatus={agentStatus}
              isDone={col.key === IssueStatus.Done}
              archivedCount={col.key === IssueStatus.Done ? archivedCount : undefined}
              headerToggle={
                isCancelled ? (
                  <Button
                    variant="ghost"
                    size="sm"
                    data-testid="cancelled-toggle"
                    onClick={() => setShowCancelled(!showCancelled)}
                    className="h-auto px-2 py-0.5 text-[11px] font-medium text-muted-foreground hover:text-foreground hover:bg-muted/80 transition-colors"
                  >
                    {showCancelled ? 'Hide cancelled' : `Show cancelled (${col.issues.length})`}
                  </Button>
                ) : undefined
              }
              bodyHidden={cancelledColumnHidden}
              emptyState={
                isCancelled
                  ? `Cancelled issues hidden — ${col.issues.length} not shown`
                  : undefined
              }
            />
          )
        })}
      </div>
    </div>
  )
}
