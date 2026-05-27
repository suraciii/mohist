import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import type { Issue, AgentStatus } from '../../../shared/api/types'
import type { SortMode } from '../model/board-query'
import { api } from '../../../shared/api/client'
import { IssueCard } from './IssueCard'
import { useProject } from '../../../entities/project/model/ProjectContext'

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
  const { projectId } = useProject()
  const [expanded, setExpanded] = useState(false)

  const totalCount = issues.length
  const hiddenCount = totalCount - DONE_COLLAPSE_LIMIT
  const shouldCollapse = isDone && hiddenCount > 0 && !expanded
  const visibleIssues = shouldCollapse
    ? issues.slice(0, DONE_COLLAPSE_LIMIT)
    : issues

  const archiveAllMutation = useMutation({
    mutationFn: () => api.archiveAllCompleted(projectId),
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
    <div className={`flex flex-col min-w-[280px] max-w-[320px] flex-1${isDone ? ' opacity-60' : ''}`}>
      <div className="mb-3 flex items-center gap-2 px-1">
        <span className={`inline-block h-2.5 w-2.5 rounded-full${isDone ? ' bg-muted' : ' bg-muted-foreground/70'}`} />
        <h2 className={`text-sm font-semibold uppercase tracking-wide${isDone ? ' text-muted-foreground/70 font-normal' : ' text-foreground/80'}`}>{label}</h2>
        <span className="ml-auto text-xs text-muted-foreground/70">{totalCount}</span>
      </div>

      {onSortChange && sort && (
        <div className="mb-2 flex items-center gap-0.5 px-1">
          {sortOptions.map((opt) => (
            <Button
              key={opt.value}
              variant={sort === opt.value ? 'default' : 'ghost'}
              size="sm"
              onClick={() => onSortChange(opt.value!)}
              className={`px-2 py-0.5 text-xs h-auto ${
                sort === opt.value
                  ? 'bg-blue-100 text-blue-700 font-medium hover:bg-blue-100'
                  : 'text-muted-foreground/70 hover:text-muted-foreground hover:bg-muted'
              }`}
            >
              {opt.label}
            </Button>
          ))}
        </div>
      )}

      <div className={`flex-1 space-y-2 overflow-y-auto rounded-lg p-2 min-h-[120px]${isDone ? ' bg-muted/60' : ' bg-muted/60'}`}>
        {totalCount === 0 && (
          <div className="flex items-center justify-center py-8 text-xs text-muted-foreground/70">
            No issues
          </div>
        )}
        {visibleIssues.map((issue) => (
          <IssueCard key={issue.id} issue={issue} agentStatus={agentStatus} showArchiveButton={isDone} />
        ))}
        {isDone && hiddenCount > 0 && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setExpanded(!expanded)}
            className="w-full text-xs font-medium text-muted-foreground hover:text-foreground/80 hover:bg-muted/80 transition-colors"
          >
            {expanded ? 'Show less' : `${hiddenCount} more`}
          </Button>
        )}
      </div>

      {isDone && totalCount > 0 && (
        <div className="mt-2 px-2 py-2 rounded-lg bg-muted/60 text-xs text-muted-foreground flex items-center justify-between">
          <span className="flex items-center gap-1">
            📦 {archivedCount} 已归档
            {archivedCount > 0 && (
              <a href="/archived" className="text-muted-foreground hover:text-foreground/80 underline ml-1">查看</a>
            )}
          </span>
          {totalCount > 0 && (
            <Button
              variant="ghost"
              size="sm"
              onClick={() => archiveAllMutation.mutate()}
              disabled={archiveAllMutation.isPending}
              className="text-muted-foreground hover:text-foreground/80 disabled:opacity-50 transition-colors h-auto py-0.5"
            >
              {archiveAllMutation.isPending ? '归档中...' : '归档所有已完成'}
            </Button>
          )}
        </div>
      )}
    </div>
  )
}