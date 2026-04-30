import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import type { Issue, AgentStatus } from '../lib/types'
import { api } from '../lib/api'
import { IssueCard } from './IssueCard'

const DONE_COLLAPSE_LIMIT = 5

interface Props {
  label: string
  issues: Issue[]
  agentStatus: AgentStatus
  isDone?: boolean
  archivedCount?: number
}

export function StageColumn({ label, issues, agentStatus, isDone, archivedCount = 0 }: Props) {
  const queryClient = useQueryClient()
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

  const archiveAllMutation = useMutation({
    mutationFn: () => api.archiveAllCompleted(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['archived-issues'] })
    },
  })

  return (
    <div className="flex flex-col min-w-[280px] max-w-[320px] flex-1">
      <div className="mb-3 flex items-center gap-2 px-1">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-gray-400" />
        <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide">{label}</h2>
        <span className="ml-auto text-xs text-gray-400">{totalCount}</span>
      </div>

      <div className="flex-1 space-y-2 overflow-y-auto rounded-lg bg-gray-100/60 p-2 min-h-[120px]">
        {totalCount === 0 && (
          <div className="flex items-center justify-center py-8 text-xs text-gray-400">
            No issues
          </div>
        )}
        {visibleIssues.map((issue) => (
          <IssueCard key={issue.id} issue={issue} agentStatus={agentStatus} showArchiveButton={isDone} />
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

      {isDone && archivedCount > 0 && (
        <div className="mt-2 px-2 py-2 rounded-lg bg-gray-100/60 text-xs text-gray-500 flex items-center justify-between">
          <span className="flex items-center gap-1">
            📦 {archivedCount} 已归档
            <a href="/archived" className="text-gray-500 hover:text-gray-700 underline ml-1">查看</a>
          </span>
          {totalCount > 0 && (
            <button
              onClick={() => archiveAllMutation.mutate()}
              disabled={archiveAllMutation.isPending}
              className="text-gray-500 hover:text-gray-700 disabled:opacity-50 transition-colors"
            >
              {archiveAllMutation.isPending ? '归档中...' : '归档所有已完成'}
            </button>
          )}
        </div>
      )}
    </div>
  )
}
