import { useState, type ReactNode } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Button } from '@/shared/ui/components/button'
import type { AgentStatus } from '../../../entities/agent'
import { issueListKeys, IssueStatus, type Issue } from '../../../entities/issue'
import { archiveAllCompleted } from '../../../entities/issue'
import { IssueCard } from './IssueCard'
import { useProject, useProjectPath } from '../../../entities/project'
import { getStageColors } from '../model/stage-colors'

const DONE_COLLAPSE_LIMIT = 5

interface Props {
  label: string
  issues: Issue[]
  agentStatus: AgentStatus
  isDone?: boolean
  archivedCount?: number
  headerToggle?: ReactNode
  footerToggle?: ReactNode
}

export function StageColumn({
  label,
  issues,
  agentStatus,
  isDone,
  archivedCount = 0,
  headerToggle,
  footerToggle,
  status,
}: Props & { status: IssueStatus }) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const toProjectPath = useProjectPath()
  const [expanded, setExpanded] = useState(false)
  const colors = getStageColors(status)

  const totalCount = issues.length
  const hiddenCount = totalCount - DONE_COLLAPSE_LIMIT
  const shouldCollapse = isDone && hiddenCount > 0 && !expanded
  const visibleIssues = shouldCollapse
    ? issues.slice(0, DONE_COLLAPSE_LIMIT)
    : issues

  const archiveAllMutation = useMutation({
    mutationFn: () => archiveAllCompleted(projectId),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      queryClient.invalidateQueries({ queryKey: issueListKeys.archived(projectId) })
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

  return (
    <div
      data-testid={`stage-column-${status}`}
      data-stage={status}
      className={`flex flex-col min-w-[280px] max-w-[420px] flex-1 rounded-xl border bg-card/50 ${
        isDone ? 'opacity-70' : ''
      } ${colors.activeBorder}`}
    >
      <div
        className="flex items-center gap-2 px-3 pt-2.5 pb-2 border-b"
        style={{ borderBottomColor: `${colors.accent}30` }}
      >
        <span
          className="inline-block h-2 w-2 rounded-full shrink-0"
          style={{ backgroundColor: colors.accent }}
        />
        <h2
          className={`text-xs font-semibold uppercase tracking-wide ${colors.labelClass}`}
        >
          {label}
        </h2>
        <span className="ml-auto text-xs text-muted-foreground tabular-nums">
          {totalCount}
        </span>
        {headerToggle}
      </div>

      <div className="flex-1 space-y-2 overflow-y-auto p-2 min-h-[120px]">
        {totalCount === 0 && (
          <div className="flex items-center justify-center py-8 text-xs text-muted-foreground/70">
            No issues
          </div>
        )}
        {visibleIssues.map((issue) => (
          <IssueCard
            key={`${issue.projectId}:${issue.number}`}
            issue={issue}
            agentStatus={agentStatus}
            showArchiveButton={isDone}
          />
        ))}
        {isDone && hiddenCount > 0 && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => setExpanded(!expanded)}
            className="w-full text-xs font-medium text-muted-foreground hover:text-foreground hover:bg-muted/80 transition-colors"
          >
            {expanded ? 'Show less' : `${hiddenCount} more`}
          </Button>
        )}
      </div>

      {footerToggle}

      {isDone && totalCount > 0 && (
        <div className="mx-2 mb-2 px-2.5 py-2 rounded-md bg-muted/60 text-xs text-muted-foreground flex items-center justify-between">
          <span className="flex items-center gap-1">
            📦 {archivedCount} archived
            {archivedCount > 0 && (
              <a
                href={toProjectPath('/archived')}
                className="text-muted-foreground hover:text-foreground/80 underline ml-1"
              >
                view
              </a>
            )}
          </span>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => archiveAllMutation.mutate()}
            disabled={archiveAllMutation.isPending}
            className="text-muted-foreground hover:text-foreground/80 disabled:opacity-50 transition-colors h-auto py-0.5"
          >
            {archiveAllMutation.isPending ? 'Archiving...' : 'Archive all done'}
          </Button>
        </div>
      )}
    </div>
  )
}
