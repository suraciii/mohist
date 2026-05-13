import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { Issue, AgentStatus } from '../lib/types'
import type { SortMode } from '../lib/board-query'
import { api } from '../lib/api'
import { IssueCard } from './IssueCard'

const DONE_COLLAPSE_LIMIT = 5

interface Props {
  label: string
  issues: Issue[]
  agentStatus: AgentStatus
  isDone?: boolean
  archivedCount?: number
  sort?: SortMode
  onSortChange?: (s: SortMode) => void
}

export function StageColumn({ label, issues, agentStatus, isDone, archivedCount = 0, sort, onSortChange }: Props) {
  const queryClient = useQueryClient()
  const [expanded, setExpanded] = useState(false)

  const totalCount = issues.length
  const hiddenCount = totalCount - DONE_COLLAPSE_LIMIT
  const shouldCollapse = isDone && hiddenCount > 0 && !expanded
  const visibleIssues = shouldCollapse
    ? issues.slice(0, DONE_COLLAPSE_LIMIT)
    : issues

  const archiveAllMutation = useMutation({
    mutationFn: () => api.archiveAllCompleted(),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['archived-issues'] })
      const parts: string[] = [`${data.archived} archived`]
      if (data.skipped > 0) {
        parts.push(`${data.skipped} skipped`)
      }
      toast.success(parts.join(', '))
      if (data.message) {
        toast.message(data.message)
      }
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Archive failed')
    },
  })

  const sortOptions: { value: typeof sort; label: string }[] = [
    { value: 'priority', label: 'Prio' },
    { value: 'number', label: '#' },
    { value: 'updated', label: 'Upd' },
  ]

  return (
    <div className="flex flex-col min-w-[280px] max-w-[320px] flex-1">
      <div className="mb-3 flex items-center gap-2 px-1">
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-gray-400" />
        <h2 className="text-sm font-semibold text-gray-700 uppercase tracking-wide">{label}</h2>
        <span className="ml-auto text-xs text-gray-400">{totalCount}</span>
      </div>

      {onSortChange && sort && (
        <div className="mb-2 flex items-center gap-0.5 px-1">
          {sortOptions.map((opt) => (
            <button
              key={opt.value}
              onClick={() => onSortChange(opt.value!)}
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
      )}

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

      {isDone && totalCount > 0 && (
        <div className="mt-2 px-2 py-2 rounded-lg bg-gray-100/60 text-xs text-gray-500 flex items-center justify-between">
          <span className="flex items-center gap-1">
            📦 {archivedCount} 已归档
            {archivedCount > 0 && (
              <a href="/archived" className="text-gray-500 hover:text-gray-700 underline ml-1">查看</a>
            )}
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
