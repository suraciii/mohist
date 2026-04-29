import { useMemo, useState, useEffect } from 'react'
import type { Issue, AgentStatus } from '../lib/types'
import { Stage } from '../lib/types'
import { groupIssuesByStage, filterClosedFromDone, getDoneColumnCounts, STAGES } from '../lib/kanban-grouping'
import { StageColumn } from './StageColumn'
import { IssueCard } from './IssueCard'

interface Props {
  issues: Issue[]
  agentStatus: AgentStatus
}

export function KanbanBoard({ issues, agentStatus }: Props) {
  const [showClosed, setShowClosed] = useState(false)

  const columns = useMemo(() => groupIssuesByStage(issues), [issues])

  const displayedColumns = useMemo(
    () => filterClosedFromDone(columns, showClosed),
    [columns, showClosed],
  )

  const { closedCount, doneTotalCount } = useMemo(
    () => getDoneColumnCounts(columns),
    [columns],
  )

  const defaultStage = useMemo(() => {
    const withIssues = columns.find((c) => c.issues.length > 0)
    return withIssues ? withIssues.key : STAGES[0].key
  }, [columns])

  const [selectedStage, setSelectedStage] = useState<Stage>(defaultStage)

  useEffect(() => {
    setSelectedStage(defaultStage)
  }, [defaultStage])

  const selectedColumn = displayedColumns.find((c) => c.key === selectedStage) ?? displayedColumns[0]

  return (
    <>
      <div className="flex items-center justify-between px-4 py-2 border-b border-gray-200 bg-white">
        <span className="text-xs text-gray-400">
          {closedCount > 0
            ? `${closedCount} closed issue${closedCount !== 1 ? 's' : ''} in Done`
            : ''}
        </span>
        <label className="flex items-center gap-2 text-xs text-gray-500 cursor-pointer select-none">
          <input
            type="checkbox"
            checked={showClosed}
            onChange={(e) => setShowClosed(e.target.checked)}
            className="rounded border-gray-300 text-gray-600 focus:ring-gray-400"
          />
          Show closed
        </label>
      </div>

      <div className="md:hidden flex flex-col h-[calc(100vh-4rem)]">
        <div className="flex overflow-x-auto snap-x snap-mandatory border-b border-gray-200 bg-white px-2 shrink-0">
          {columns.map((col) => (
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

      <div className="hidden md:flex gap-4 overflow-x-auto p-4 h-[calc(100vh-4rem)]">
        {displayedColumns.map((col) => (
          <StageColumn
            key={col.key}
            label={col.label}
            issues={col.issues}
            agentStatus={agentStatus}
            isDone={col.key === Stage.Done}
            displayCount={col.key === Stage.Done ? doneTotalCount : undefined}
          />
        ))}
      </div>
    </>
  )
}
