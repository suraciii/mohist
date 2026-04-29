import { useState } from 'react'
import type { Issue, AgentStatus } from '../lib/types'
import { IssueCard } from './IssueCard'

const DONE_COLLAPSE_LIMIT = 5

interface Props {
  label: string
  issues: Issue[]
  agentStatus: AgentStatus
  isDone?: boolean
  displayCount?: number
}

export function StageColumn({ label, issues, agentStatus, isDone, displayCount }: Props) {
  const [expanded, setExpanded] = useState(false)

  const sortedIssues = isDone
    ? [...issues].sort(
        (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime(),
      )
    : issues

  const totalCount = sortedIssues.length
  const hiddenCount = totalCount - DONE_COLLAPSE_LIMIT
  const shouldCollapse = isDone && hiddenCount > 0 && !expanded
  const visibleIssues = shouldCollapse
    ? sortedIssues.slice(0, DONE_COLLAPSE_LIMIT)
    : sortedIssues

  return (
    <div className="flex flex-col min-w-[280px] max-w-[320px] flex-1">
      <div className="mb-3 flex items-center gap-2 px-1">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-gray-400" />
        <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide">{label}</h2>
        <span className="ml-auto text-xs text-gray-400">{displayCount ?? totalCount}</span>
      </div>

      <div className="flex-1 space-y-2 overflow-y-auto rounded-lg bg-gray-100/60 p-2 min-h-[120px]">
        {totalCount === 0 && (
          <div className="flex items-center justify-center py-8 text-xs text-gray-400">
            No issues
          </div>
        )}
        {visibleIssues.map((issue) => (
          <IssueCard key={issue.id} issue={issue} agentStatus={agentStatus} />
        ))}
        {isDone && hiddenCount > 0 && (
          <button
            onClick={() => setExpanded(!expanded)}
            className="w-full rounded-md px-3 py-1.5 text-xs font-medium text-gray-500 hover:text-gray-700 hover:bg-gray-200/60 transition-colors"
          >
            {expanded ? 'Show less' : `${hiddenCount} more`}
          </button>
        )}
      </div>
    </div>
  )
}
