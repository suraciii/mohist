import { useMemo, useState, useEffect } from 'react'
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

interface Props {
  issues: Issue[]
  agentStatus: AgentStatus
  archivedCount?: number
}

export function KanbanBoard({ issues, agentStatus, archivedCount = 0 }: Props) {
  const [showClosed, setShowClosed] = useState(false)

  const columns = useMemo(() => groupIssuesByStage(issues), [issues])

  const { closedCount } = useMemo(
    () => getDoneColumnCounts(columns),
    [columns],
  )

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
    <>
      <div className="md:hidden flex flex-col h-[calc(100vh-4rem)]">
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

      <div className="hidden md:flex gap-4 overflow-x-auto p-4 h-[calc(100vh-4rem)]">
        {displayedColumns.map((col) => (
          <StageColumn
            key={col.key}
            label={col.label}
            issues={col.issues}
            agentStatus={agentStatus}
            isDone={col.key === Stage.Done}
            archivedCount={col.key === Stage.Done ? archivedCount : undefined}
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
    </>
  )
}