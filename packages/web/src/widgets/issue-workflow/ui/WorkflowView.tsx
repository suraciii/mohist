import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { MessageSquareIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { approveIssue, rejectIssue, resumeIssue, startIssue, getFileContent } from '../../../entities/issue'
import { onAgentEvent } from '../../../entities/agent'
import { IssueStatus, WorkflowStage, IssueHealth } from '../../../entities/issue'
import type { Issue, StageTaskState, StageCheckState, StageStateRead, CheckRepairState, CheckRepairStatus, WorkItemOrigin } from '../../../entities/issue'
import type { AgentDetailEventMap } from '../../../entities/agent'
import { useWorkflowTimeline } from '../../../entities/issue'
import { useProject, useProjectPath } from '../../../entities/project'
import { ReviewSummary, parseReviewOutput } from './ReviewSummary'
import type { ReviewOutput } from './ReviewSummary'
import { FullReportModal } from './ReviewReportModal'

function classifyResult(result?: string): 'PASS' | 'FAIL' | 'UNKNOWN' {
  if (!result) return 'UNKNOWN'
  const upper = result.toUpperCase()
  if (upper === 'PASS') return 'PASS'
  if (upper === 'FAIL') return 'FAIL'
  return 'UNKNOWN'
}

const WORKFLOW_STAGES = [WorkflowStage.Plan, WorkflowStage.Build, WorkflowStage.Check, WorkflowStage.Integrate, WorkflowStage.Done] as const

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  const m = Math.floor(ms / 60000)
  const s = Math.floor((ms % 60000) / 1000)
  return `${m}m ${s}s`
}

function getStageStatus(
  stage: WorkflowStage,
  stageStateMap: Map<string, StageStateRead>,
  issue: Issue,
): 'pending' | 'running' | 'completed' | 'failed' | 'awaiting-approval' {
  const stageState = stageStateMap.get(stage)
  const stageOrder = WORKFLOW_STAGES.indexOf(stage)
  const currentStageIdx = issue.workflowStage ? WORKFLOW_STAGES.indexOf(issue.workflowStage) : -1

  if (stageState) {
    if (stageState.status === 'running') return 'running'
    if (stageState.status === 'awaiting-approval') return 'awaiting-approval'
    if (stageState.status === 'completed' || stageState.status === 'passed') return 'completed'
    if (stageState.status === 'failed') return 'failed'
    if (stageState.status === 'skipped') return 'pending'
  }

  if (issue.workflowStage === stage && !stageState) return 'running'

  if (issue.status === IssueStatus.Done && stage === WorkflowStage.Done) return 'completed'

  if (currentStageIdx < 0 || stageOrder > currentStageIdx) return 'pending'

  return 'pending'
}

function getStageDuration(stage: WorkflowStage, stageStateMap: Map<string, StageStateRead>): number | null {
  const stageState = stageStateMap.get(stage)
  if (!stageState) return null
  if (stageState.startedAt && stageState.completedAt) {
    const started = new Date(stageState.startedAt).getTime()
    const completed = new Date(stageState.completedAt).getTime()
    if (!Number.isNaN(started) && !Number.isNaN(completed)) {
      return Math.max(0, completed - started)
    }
  }
  if (stageState.tasks.length === 0) return null
  const total = stageState.tasks.reduce((sum, t) => sum + (t.duration || 0), 0)
  return total > 0 ? total : null
}

function workflowTimelineToStageStateMap(timeline: ReturnType<typeof useWorkflowTimeline>['data']): Map<string, StageStateRead> {
  const map = new Map<string, StageStateRead>()
  if (!timeline) return map

  for (const stage of timeline.stages) {
    map.set(stage.stage, {
      stage: stage.stage,
      status: stage.status,
      tasks: stage.tasks.map((task, index) => ({
        taskId: task.id,
        title: task.title,
        status: task.status,
        sessionName: task.sessionName,
        order: index,
        attempts: task.attempts,
        duration: task.durationMs ?? 0,
        artifacts: [],
        output: task.message,
        startedAt: task.startedAt,
        completedAt: task.completedAt,
        updatedAt: task.completedAt ?? task.startedAt ?? '',
        reason: task.message ?? undefined,
        origin: task.uses ? { source: 'runtime', uses: task.uses } : null,
        requiredFiles: task.requiredFiles,
        classification: task.classification,
      })),
      checks: stage.checks.map((check) => ({
        checkName: check.name,
        title: check.title,
        status: check.status as StageCheckState['status'],
        message: check.message,
        output: null,
        runCount: 1,
        lastRunAt: check.completedAt ?? check.startedAt,
        origin: check.uses ? { source: 'runtime', uses: check.uses } : null,
        updatedAt: check.completedAt ?? check.startedAt ?? '',
      })),
      approval: stage.approval,
      attempts: 1,
      startedAt: stage.startedAt,
      completedAt: stage.completedAt,
      updatedAt: stage.completedAt ?? stage.startedAt ?? '',
    })
  }

  return map
}

function CheckmarkIcon({ className = 'h-5 w-5 text-green-500' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function CrossIcon({ className = 'h-5 w-5 text-red-500' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function SpinnerIcon({ className = 'h-5 w-5 text-blue-500 animate-spin' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="none">
      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
    </svg>
  )
}

function EmptyCircleIcon({ className = 'h-5 w-5 text-gray-300' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zm0-2a6 6 0 100-12 6 6 0 000 12z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function HourglassIcon({ className = 'h-5 w-5 text-amber-500' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zm.75-13a.75.75 0 00-1.5 0v5c0 .414.336.75.75.75h3a.75.75 0 000-1.5h-2.25V5z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function InterruptedIcon({ className = 'h-5 w-5 text-orange-500' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a.75.75 0 000 1.5h.25V15a.75.75 0 001.5 0v-4a.75.75 0 00-.75-.75H9z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function StageStatusIcon({ status }: { status: string }) {
  switch (status) {
    case 'completed':
      return <CheckmarkIcon />
    case 'running':
      return <SpinnerIcon />
    case 'failed':
      return <CrossIcon />
    case 'awaiting-approval':
      return <HourglassIcon />
    default:
      return <EmptyCircleIcon />
  }
}

function StageBarCell({
  stage,
  status,
  duration,
  selected,
  readOnly,
  onClick,
}: {
  stage: WorkflowStage
  status: string
  duration: number | null
  selected: boolean
  readOnly: boolean
  onClick: () => void
}) {
  const bgColor = selected ? 'bg-muted border-gray-300' : 'bg-background border'
  const stageLabel = stage.charAt(0).toUpperCase() + stage.slice(1)

  return (
    <Button
      variant="ghost"
      onClick={onClick}
      disabled={readOnly && status === 'pending'}
      className={`flex-1 min-w-0 rounded-lg border p-3 text-left transition-colors h-auto justify-start font-normal ${bgColor} ${
        !readOnly && status !== 'pending' ? 'cursor-pointer hover:bg-muted' : ''
      } ${status === 'pending' && !selected ? 'opacity-60' : ''}`}
    >
      <div className="flex items-center gap-2 mb-1">
        <StageStatusIcon status={status} />
        <span className="text-sm font-medium text-foreground truncate">{stageLabel}</span>
      </div>
      {status === 'completed' && duration != null && (
        <span className="text-xs text-muted-foreground/70 ml-7">{formatDuration(duration)}</span>
      )}
      {status === 'running' && duration != null && (
        <span className="text-xs text-blue-500 ml-7">{formatDuration(duration)}</span>
      )}
    </Button>
  )
}

function StageBar({
  stageStateMap,
  issue,
  selectedStage,
  onSelectStage,
  readOnly,
  runningDurations,
}: {
  stageStateMap: Map<string, StageStateRead>
  issue: Issue
  selectedStage: WorkflowStage
  onSelectStage: (stage: WorkflowStage) => void
  readOnly: boolean
  runningDurations: Map<string, number>
}) {
  return (
    <div className="flex items-stretch gap-2">
      {WORKFLOW_STAGES.map((stage, idx) => {
        const status = getStageStatus(stage, stageStateMap, issue)
        let duration = getStageDuration(stage, stageStateMap)
        if (status === 'running' && runningDurations.has(stage)) {
          duration = runningDurations.get(stage)!
        }
        return (
          <div key={stage} className="flex items-stretch flex-1 min-w-0">
            {idx > 0 && (
              <div className="flex items-center px-1">
                <svg className="h-4 w-4 text-gray-300 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
                  <path
                    fillRule="evenodd"
                    d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
                    clipRule="evenodd"
                  />
                </svg>
              </div>
            )}
            <StageBarCell
              stage={stage}
              status={status}
              duration={duration}
              selected={selectedStage === stage}
              readOnly={readOnly}
              onClick={() => onSelectStage(stage)}
            />
          </div>
        )
      })}
    </div>
  )
}

function RequiredFileEntry({ rf, issueNumber }: { rf: { path: string; source: string; canFetchContent: boolean; markers?: string[] }; issueNumber: number }) {
  const [open, setOpen] = useState(false)
  const [loading, setLoading] = useState(false)
  const [content, setContent] = useState<string | null>(null)
  const [error, setError] = useState(false)
  const { projectId } = useProject()

  const handleToggle = useCallback(() => {
    if (open) {
      setOpen(false)
    } else {
      setOpen(true)
      if (!content && !loading && rf.canFetchContent) {
        setLoading(true)
        setError(false)
        getFileContent(issueNumber, rf.path, projectId)
          .then((resp) => setContent(resp.head || resp.base))
          .catch(() => setError(true))
          .finally(() => setLoading(false))
      }
    }
  }, [open, content, loading, rf.canFetchContent, rf.path, issueNumber, projectId])

  return (
    <div className="text-xs">
      <button
        onClick={handleToggle}
        disabled={!rf.canFetchContent}
        className="flex items-center gap-1.5 text-muted-foreground hover:text-foreground disabled:opacity-50 transition-colors"
      >
        <svg className="h-3 w-3 flex-shrink-0 text-blue-500" viewBox="0 0 20 20" fill="currentColor">
          <path d="M3 3.5A1.5 1.5 0 014.5 2h6.879a1.5 1.5 0 011.06.44l4.122 4.12A1.5 1.5 0 0117 7.622V16.5a1.5 1.5 0 01-1.5 1.5h-11A1.5 1.5 0 013 16.5v-13z" />
        </svg>
        <span className="font-mono truncate">{rf.path}</span>
        {rf.source === 'task-expect' && <span className="text-[10px] text-blue-400 flex-shrink-0">expect</span>}
      </button>
      {open && (
        <div className="mt-1 rounded bg-gray-100 p-2 max-h-60 overflow-auto">
          {loading && <span className="text-gray-400">loading...</span>}
          {error && <span className="text-red-400">File content unavailable</span>}
          {content && <pre className="whitespace-pre-wrap break-words font-mono text-xs text-gray-600">{content}</pre>}
        </div>
      )}
    </div>
  )
}

function TaskSessionChip({ issueNumber, sessionName }: { issueNumber: number; sessionName: string }) {
  const toProjectPath = useProjectPath()
  const transcriptPath = toProjectPath(`/issues/${issueNumber}/workflow/sessions/${encodeURIComponent(sessionName)}`)

  return (
    <Link
      to={transcriptPath}
      onClick={(event) => event.stopPropagation()}
      className="inline-flex items-center gap-1 rounded border border-blue-100 bg-blue-50 px-1.5 py-0.5 text-[11px] font-medium text-blue-700 hover:border-blue-200 hover:bg-blue-100 shrink-0"
      title={`Open ${sessionName} transcript`}
    >
      <MessageSquareIcon className="h-3 w-3" aria-hidden="true" />
      <span>{sessionName}</span>
    </Link>
  )
}

function TaskItem({
  task,
  issueNumber,
  readOnly,
  liveElapsed,
}: {
  task: StageTaskState
  issueNumber: number
  readOnly: boolean
  liveElapsed?: number | null
}) {
  const [expanded, setExpanded] = useState(false)
  const isPending = task.status === 'pending'
  const isRunning = task.status === 'running'
  const isFailed = task.status === 'failed'
  const taskOutput = task.output
  const hasOutput = taskOutput != null
  const hasRequiredFiles = (task.requiredFiles?.length ?? 0) > 0
  const canExpand = task.artifacts.length > 0 || hasRequiredFiles || isFailed || hasOutput

  let icon: React.ReactNode
  if (task.status === 'completed') {
    icon = <CheckmarkIcon className="h-4 w-4 text-green-500 flex-shrink-0" />
  } else if (isFailed) {
    icon = <CrossIcon className="h-4 w-4 text-red-500 flex-shrink-0" />
  } else if (isRunning) {
    icon = <SpinnerIcon className="h-4 w-4 text-blue-500 animate-spin flex-shrink-0" />
  } else {
    icon = <EmptyCircleIcon className="h-4 w-4 text-gray-300 flex-shrink-0" />
  }

  const duration =
    task.status === 'completed' || task.status === 'failed'
      ? task.duration
      : liveElapsed

  const hasReason = task.reason != null
  const originLabel = formatOriginLabel(task.origin)
  const originTitle = formatOriginTitle(task.origin)
  const sessionName = task.sessionName?.trim()

  return (
    <div
      className={`rounded-md border overflow-hidden ${isPending ? 'opacity-50' : ''} ${isFailed ? 'border-red-200' : ''}`}
    >
      <Button
        variant="ghost"
        onClick={() => !readOnly && canExpand && setExpanded(!expanded)}
        disabled={readOnly}
        className="w-full flex items-center gap-2 px-3 py-2 text-left hover:bg-muted transition-colors h-auto justify-start font-normal"
      >
        {icon}
        <span className="text-sm text-gray-900 flex-1 truncate">{task.title}</span>
        {hasReason && (
          <span className="text-xs text-amber-500 flex-shrink-0" title={task.reason}>reason</span>
        )}
        {originLabel && (
          <span className="text-[11px] text-gray-400 flex-shrink-0 font-mono" title={originTitle}>{originLabel}</span>
        )}
        {sessionName && (
          <TaskSessionChip issueNumber={issueNumber} sessionName={sessionName} />
        )}
        {duration != null && duration > 0 && (
          <span className="text-xs text-gray-400 flex-shrink-0">{formatDuration(duration)}</span>
        )}
        {isFailed && (
          <span className="text-xs text-red-500 flex-shrink-0">failed</span>
        )}
        {canExpand && !readOnly && (
          <svg
            className={`h-3 w-3 text-gray-400 transition-transform flex-shrink-0 ${expanded ? 'rotate-180' : ''}`}
            viewBox="0 0 20 20"
            fill="currentColor"
          >
            <path
              fillRule="evenodd"
              d="M5.23 7.21a.75.75 0 011.06.02L10 10.94l3.71-3.71a.75.75 0 111.06 1.06l-4.24 4.24a.75.75 0 01-1.06 0L5.23 8.27a.75.75 0 01.02-1.06z"
              clipRule="evenodd"
            />
          </svg>
        )}
      </Button>
      {expanded && canExpand && (
        <div className="px-3 pb-2 border-t bg-muted">
          <div className="mt-2 space-y-1">
            {hasReason && (
              <div className="text-xs text-amber-600 bg-amber-50 rounded px-2 py-1">
                {task.reason}
              </div>
            )}
            {task.requiredFiles?.map((rf) => (
              <RequiredFileEntry key={rf.path} rf={rf} issueNumber={issueNumber} />
            ))}
            {hasOutput && (
              <pre className="text-xs text-gray-600 whitespace-pre-wrap break-words font-mono bg-gray-100 rounded p-2 max-h-40 overflow-auto">
                {typeof taskOutput === 'string' ? taskOutput : JSON.stringify(taskOutput, null, 2)}
              </pre>
            )}
          </div>
        </div>
      )}
    </div>
  )
}

function isScriptHealthCheck(check: StageCheckState): boolean {
  const output = check.output as { kind?: string } | undefined
  return check.checkName === 'health' || output?.kind === 'script'
}

function CheckItem({ check, attemptLabel }: { check: StageCheckState; attemptLabel?: string }) {
  const isPending = check.status === 'pending'
  const isFailed = check.status === 'failed' || check.status === 'error'
  const isHealthCheck = isScriptHealthCheck(check)
  const healthOutput = check.output as { command?: string; duration?: number; summary?: string; logExcerpt?: string; enabled?: boolean; exitCode?: number; timedOut?: boolean } | undefined

  let icon: React.ReactNode
  if (check.status === 'completed' || check.status === 'passed') {
    icon = <CheckmarkIcon className="h-4 w-4 text-green-500 flex-shrink-0" />
  } else if (isFailed) {
    icon = <CrossIcon className="h-4 w-4 text-red-500 flex-shrink-0" />
  } else if (check.status === 'running') {
    icon = <SpinnerIcon className="h-4 w-4 text-blue-500 animate-spin flex-shrink-0" />
  } else {
    icon = <EmptyCircleIcon className="h-4 w-4 text-gray-300 flex-shrink-0" />
  }

  const fallbackName = isHealthCheck ? 'Health check' : check.checkName
  const baseName = check.title?.trim() || fallbackName
  const displayName = attemptLabel ? `${baseName} (${attemptLabel})` : baseName
  const originLabel = formatOriginLabel(check.origin)
  const originTitle = formatOriginTitle(check.origin)

  return (
    <div
      className={`flex items-center gap-2 px-3 py-2 rounded-md border ${isHealthCheck && isFailed ? 'border-red-200 bg-red-50' : ''} ${isPending ? 'opacity-50' : ''}`}
    >
      {icon}
      <span className="text-sm text-gray-900 flex-1 truncate">{displayName}</span>
      {isFailed && check.message && (
        <span className="text-xs text-red-500 flex-shrink-0 truncate max-w-48">{check.message}</span>
      )}
      {originLabel && (
        <span className="text-[11px] text-gray-400 flex-shrink-0 font-mono" title={originTitle}>{originLabel}</span>
      )}
      {isHealthCheck && healthOutput && (
        <>
          {healthOutput.command && (
            <span className="text-xs text-gray-400 flex-shrink-0 font-mono truncate max-w-32" title={healthOutput.command}>{healthOutput.command}</span>
          )}
          {healthOutput.duration != null && (
            <span className="text-xs text-gray-400 flex-shrink-0">{formatDuration(healthOutput.duration)}</span>
          )}
          {isFailed && healthOutput.summary && (
            <span className="text-xs text-red-400 flex-shrink-0 truncate max-w-48" title={healthOutput.summary}>{healthOutput.summary}</span>
          )}
        </>
      )}
    </div>
  )
}

function formatOriginLabel(origin?: WorkItemOrigin | null): string | null {
  if (!origin) return null
  const source = origin.source === 'builtin' ? 'built-in' : origin.source
  return `${source}:${origin.uses.replace(/^mohist\//, '')}`
}

function formatOriginTitle(origin?: WorkItemOrigin | null): string | undefined {
  if (!origin) return undefined
  return `${origin.source} workflow item using ${origin.uses}`
}

function InlineApproval({
  issueNumber,
  stage,
  readOnly,
  approvalOutput,
}: {
  issueNumber: number
  stage: WorkflowStage
  readOnly: boolean
  approvalOutput?: Record<string, unknown>
}) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const [feedback, setFeedback] = useState('')
  const [reportModalOpen, setReportModalOpen] = useState(false)
  const [instructionsExpanded, setInstructionsExpanded] = useState(false)
  const [instructionsText, setInstructionsText] = useState('')
  const [notesExpanded, setNotesExpanded] = useState(false)
  const [notesText, setNotesText] = useState('')

  const review: ReviewOutput = useMemo(() => parseReviewOutput(approvalOutput), [approvalOutput])
  const classified = useMemo(() => classifyResult(review.result), [review.result])

  const approveMutation = useMutation({
    mutationFn: () => approveIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  })

  const rejectMutation = useMutation({
    mutationFn: () => rejectIssue(issueNumber, { message: feedback.trim() || undefined }, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      setFeedback('')
    },
  })

  const sendBackMutation = useMutation({
    mutationFn: (message: string) => rejectIssue(issueNumber, { message }, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      setInstructionsText('')
      setInstructionsExpanded(false)
      setNotesText('')
      setNotesExpanded(false)
    },
  })

  const handleSendBackForFixes = useCallback(() => {
    const failDims = (review.dimensions ?? []).filter(
      (d) => d.status.toUpperCase() === 'FAIL',
    )
    if (failDims.length > 0) {
      const parts = failDims.map((dim) => {
        const issues = dim.issues && dim.issues.length > 0
          ? dim.issues.map((i) => `- ${i}`).join('\n')
          : '- Issues identified in this dimension'
        return `### ${dim.name}\n${issues}`
      })
      sendBackMutation.mutate(`Please fix the following issues:\n\n${parts.join('\n\n')}`)
    } else if (review.reviewReport) {
      sendBackMutation.mutate(`Please fix the following issues:\n\n${review.reviewReport}`)
    } else {
      sendBackMutation.mutate('The review found issues that need to be addressed. Please review and fix all problems.')
    }
  }, [review, sendBackMutation])

  const handleSendWithInstructions = useCallback(() => {
    if (!instructionsText.trim()) return
    sendBackMutation.mutate(instructionsText.trim())
  }, [instructionsText, sendBackMutation])

  const handleSendBackWithNotes = useCallback(() => {
    if (!notesText.trim()) return
    sendBackMutation.mutate(notesText.trim())
  }, [notesText, sendBackMutation])

  const handleApproveAnyway = useCallback(() => {
    approveMutation.mutate()
  }, [approveMutation])

  const handleViewChanges = useCallback(() => {
    document.getElementById('changes-panel')?.scrollIntoView({ behavior: 'smooth' })
  }, [])

  const getApproveLabel = () => {
    if (stage === WorkflowStage.Plan) return 'Approve & Continue'
    if (stage === WorkflowStage.Check) return 'Approve & Continue'
    return 'Approve & Continue'
  }

  if (readOnly) return null

  const hasApprovalOutput = approvalOutput != null

  return (
    <div className="rounded-lg border border-amber-200 bg-amber-50 p-4 space-y-3">
      {reportModalOpen && (
        <FullReportModal
          review={review}
          classified={classified}
          onClose={() => setReportModalOpen(false)}
        />
      )}

      <h3 className="text-sm font-semibold text-amber-800">Approval Required</h3>
      <p className="text-xs text-amber-600">
        {stage === WorkflowStage.Plan
          ? 'Review the design proposal and approve to continue the workflow.'
          : stage === WorkflowStage.Check
            ? 'Review the check results and approve to continue the workflow.'
            : `Review the ${stage} stage output and approve to continue, or send back with feedback.`}
      </p>

      {hasApprovalOutput && (
        <ReviewSummary output={approvalOutput} />
      )}

      {hasApprovalOutput && (
        <div className="flex gap-4 text-xs">
          <Button
            variant="link"
            onClick={() => setReportModalOpen(true)}
            className="h-auto p-0 text-xs"
          >
            View Full Report
          </Button>
          <Button
            variant="link"
            onClick={handleViewChanges}
            className="h-auto p-0 text-xs"
          >
            View Changes
          </Button>
        </div>
      )}

      {!hasApprovalOutput && (
        <div className="space-y-2">
          <div className="flex gap-2">
            <Button
              onClick={() => approveMutation.mutate()}
              disabled={approveMutation.isPending}
              className="flex-1"
            >
              {approveMutation.isPending ? 'Approving...' : getApproveLabel()}
            </Button>
            <Button
              variant="outline"
              onClick={() => rejectMutation.mutate()}
              disabled={rejectMutation.isPending}
              className="flex-1"
            >
              {rejectMutation.isPending ? 'Sending...' : 'Send back'}
            </Button>
          </div>
          <Textarea
            value={feedback}
            onChange={(e) => setFeedback(e.target.value)}
            placeholder="Optional feedback..."
            rows={2}
            className="resize-none"
          />
        </div>
      )}

      {hasApprovalOutput && classified === 'PASS' && (
        <div className="space-y-2">
          <Button
            onClick={() => approveMutation.mutate()}
            disabled={approveMutation.isPending}
            className="w-full bg-green-600 hover:bg-green-700 text-white"
          >
            {approveMutation.isPending ? 'Approving...' : getApproveLabel()}
          </Button>
        </div>
      )}

      {hasApprovalOutput && classified === 'FAIL' && (
        <div className="space-y-2">
          <Button
            variant="destructive"
            onClick={handleSendBackForFixes}
            disabled={sendBackMutation.isPending}
            className="w-full"
          >
            {sendBackMutation.isPending ? 'Sending back...' : 'Send back for fixes'}
          </Button>

          <div>
            <Button
              variant="ghost"
              onClick={() => setInstructionsExpanded(!instructionsExpanded)}
              className="h-auto px-0 text-sm text-muted-foreground hover:text-foreground"
            >
              {instructionsExpanded ? '▾' : '▸'} Add instructions...
            </Button>
            {instructionsExpanded && (
              <div className="mt-2 space-y-2">
                <Textarea
                  value={instructionsText}
                  onChange={(e) => setInstructionsText(e.target.value)}
                  placeholder="Add your instructions for the fix..."
                  rows={3}
                  className="resize-none"
                />
                <Button
                  onClick={handleSendWithInstructions}
                  disabled={!instructionsText.trim() || sendBackMutation.isPending}
                >
                  {sendBackMutation.isPending ? 'Sending back...' : 'Send with instructions'}
                </Button>
              </div>
            )}
          </div>

          <Button
            variant="outline"
            onClick={handleApproveAnyway}
            disabled={approveMutation.isPending}
            className="w-full"
          >
            {approveMutation.isPending ? 'Approving...' : 'Approve anyway'}
          </Button>
        </div>
      )}

      {hasApprovalOutput && classified === 'UNKNOWN' && (
        <div className="space-y-2">
          <Button
            onClick={() => approveMutation.mutate()}
            disabled={approveMutation.isPending}
            className="w-full"
          >
            {approveMutation.isPending ? 'Approving...' : getApproveLabel()}
          </Button>

          <div>
            <Button
              variant="ghost"
              onClick={() => setNotesExpanded(!notesExpanded)}
              className="h-auto px-0 text-sm text-muted-foreground hover:text-foreground"
            >
              {notesExpanded ? '▾' : '▸'} Send back with notes...
            </Button>
            {notesExpanded && (
              <div className="mt-2 space-y-2">
                <Textarea
                  value={notesText}
                  onChange={(e) => setNotesText(e.target.value)}
                  placeholder="Describe what needs to be changed..."
                  rows={3}
                  className="resize-none"
                />
                <Button
                  onClick={handleSendBackWithNotes}
                  disabled={!notesText.trim() || sendBackMutation.isPending}
                >
                  {sendBackMutation.isPending ? 'Sending back...' : 'Send back'}
                </Button>
              </div>
            )}
          </div>
        </div>
      )}

      {(approveMutation.error || rejectMutation.error || sendBackMutation.error) && (
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          {approveMutation.error?.message || rejectMutation.error?.message || sendBackMutation.error?.message}
        </div>
      )}
    </div>
  )
}

function StepList({
  stage,
  stageStateMap,
  issue,
  readOnly,
  liveElapsedByTask,
}: {
  stage: WorkflowStage
  stageStateMap: Map<string, StageStateRead>
  issue: Issue
  readOnly: boolean
  liveElapsedByTask: Map<string, number>
}) {
  const stageState = stageStateMap.get(stage)
  const taskResults: StageTaskState[] = stageState?.tasks ?? []
  const checkResults: StageCheckState[] = stageState?.checks ?? []

  const scriptHealthChecks = checkResults.filter(c =>
    c.checkName === 'health' || (c.output && (c.output as Record<string, unknown>).kind === 'script')
  )
  const failedScriptHealthChecks = scriptHealthChecks.filter(c => c.status === 'failed' || c.status === 'error')

  const isAwaitingApproval =
    failedScriptHealthChecks.length === 0 &&
    issue.approvalState?.status === 'awaiting' &&
    issue.workflowStage === stage

  return (
    <div className="space-y-4">
      {stage !== WorkflowStage.Done && (
        <div>
          <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-2">Tasks</h3>
          <div className="space-y-1.5">
            {taskResults.length > 0 ? (
              taskResults.map((task) => (
                <TaskItem
                  key={task.taskId}
                  task={task}
                  issueNumber={issue.number}
                  readOnly={readOnly}
                  liveElapsed={liveElapsedByTask.get(task.taskId)}
                />
              ))
            ) : (
              <div className="text-sm text-gray-400 py-2">No tasks yet</div>
            )}
          </div>
        </div>
      )}

      {checkResults.length > 0 && stage !== WorkflowStage.Done && (
        <div>
          <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-2">Checks</h3>
          <div className="space-y-1.5">
            {(() => {
              const nameCounts = new Map<string, number>()
              for (const c of checkResults) {
                nameCounts.set(c.checkName, (nameCounts.get(c.checkName) ?? 0) + 1)
              }
              const nameSeen = new Map<string, number>()
              return checkResults.map((check, idx) => {
                const total = nameCounts.get(check.checkName) ?? 1
                const seen = (nameSeen.get(check.checkName) ?? 0) + 1
                nameSeen.set(check.checkName, seen)
                const attemptLabel = total > 1 ? `attempt ${seen}` : undefined
                return <CheckItem key={`${check.checkName}-${idx}`} check={check} attemptLabel={attemptLabel} />
              })
            })()}
          </div>
        </div>
      )}

      {isAwaitingApproval && (
        <div className="space-y-3">
          {checkResults.length === 0 && (
            <div className="rounded-md border border-orange-200 bg-orange-50 px-3 py-2 text-xs text-orange-700">
              Approval is awaiting, but this stage has no recorded check results. This usually means the issue was recovered from an interrupted state; rerun the stage if you need fresh verification before approving.
            </div>
          )}
          <InlineApproval issueNumber={issue.number} stage={stage} readOnly={readOnly} approvalOutput={issue.approvalState?.output} />
        </div>
      )}

      {!isAwaitingApproval && stage === WorkflowStage.Check && failedScriptHealthChecks.length > 0 && (
        <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
          <span className="font-semibold">Full verification failed:</span> Check approval is blocked until the health check passes. Fix the failures and rerun Check.
        </div>
      )}

      {!isAwaitingApproval && stage === WorkflowStage.Check && scriptHealthChecks.length > 0 && scriptHealthChecks.every(c => c.status === 'pending') && (
        <div className="rounded-md border border-yellow-200 bg-yellow-50 px-3 py-2 text-xs text-yellow-700">
          Full verification has not run yet. Approval will be available once verification completes.
        </div>
      )}
    </div>
  )
}

function SpecialStatePanel({
  issue,
  issueNumber,
  readOnly,
}: {
  issue: Issue
  issueNumber: number
  readOnly: boolean
}) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()

  const startMutation = useMutation({
    mutationFn: () => startIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const resumeMutation = useMutation({
    mutationFn: () => resumeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  if (readOnly) return null

  if (issue.status === IssueStatus.Backlog) {
    return (
      <div className="flex justify-center py-4">
        <Button
          onClick={() => startMutation.mutate()}
          disabled={startMutation.isPending}
          className="px-6"
        >
          {startMutation.isPending ? 'Starting...' : 'Start'}
        </Button>
      </div>
    )
  }

  if (issue.health === IssueHealth.Blocked) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-4 space-y-2">
        <div className="flex items-center gap-2">
          <CrossIcon className="h-4 w-4 text-red-500" />
          <span className="text-sm font-semibold text-red-800">Needs Action</span>
        </div>
        {issue.blockedReason && (
          <p className="text-sm text-red-600">{issue.blockedReason}</p>
        )}
      </div>
    )
  }

  if (issue.health === IssueHealth.Interrupted) {
    return (
      <div className="rounded-lg border border-orange-200 bg-orange-50 p-4 space-y-3">
        <div className="flex items-center gap-2">
          <InterruptedIcon />
          <span className="text-sm font-semibold text-orange-800">Workflow Interrupted</span>
        </div>
        <p className="text-xs text-orange-600">
          The workflow was interrupted. Click &quot;Resume&quot; to continue from where it left off.
        </p>
        <Button
          onClick={() => resumeMutation.mutate()}
          disabled={resumeMutation.isPending}
          className="bg-orange-500 hover:bg-orange-600 text-white"
        >
          {resumeMutation.isPending ? 'Resuming...' : 'Resume'}
        </Button>
      </div>
    )
  }

  return null
}

export function CheckRepairPanel({ checkRepair }: { checkRepair: CheckRepairState }) {
  const statusLabels: Record<CheckRepairStatus, string> = {
    'not-needed': 'Repair not needed',
    'available': 'Auto-fix available',
    'pending': 'Repair pending',
    'running': 'Repair running',
    'completed': 'Repair completed',
    'exhausted': 'Auto-fix exhausted',
  }

  const stopReasonLabels: Record<string, string> = {
    'review-passed': 'Review passed',
    'repair-pending': 'Waiting for repair to start',
    'repair-running': 'Repair in progress',
    'max-repair-attempts-reached': 'Max repair attempts reached',
    'manual-rerun-required': 'Manual review required',
  }

  return (
    <div className="rounded-lg border border-red-200 bg-red-50 p-4 space-y-3">
      <div className="flex items-center gap-2">
        <CrossIcon className="h-4 w-4 text-red-500" />
        <span className="text-sm font-semibold text-red-800">Check failed: {checkRepair.checkName}</span>
      </div>

      <div className="space-y-1.5 text-xs text-red-700">
        <div className="flex items-center justify-between">
          <span className="font-medium">Auto-fix status:</span>
          <span className={checkRepair.status === 'exhausted' ? 'text-red-600 font-medium' : ''}>
            {statusLabels[checkRepair.status] ?? checkRepair.status}
          </span>
        </div>

        <div className="flex items-center justify-between">
          <span className="font-medium">Attempts:</span>
          <span>
            {checkRepair.attemptsUsed} used, {checkRepair.attemptsRemaining} remaining (max {checkRepair.attemptsMax})
          </span>
        </div>

        {checkRepair.lastRepairStatus && (
          <div className="flex items-center justify-between">
            <span className="font-medium">Last repair:</span>
            <span className={checkRepair.lastRepairStatus === 'completed' ? 'text-green-600' : ''}>
              {checkRepair.lastRepairStatus === 'completed' ? 'completed' : checkRepair.lastRepairStatus}
              {checkRepair.followUpReviewStatus === 'failed' && ' — follow-up check failed'}
            </span>
          </div>
        )}

        {checkRepair.followUpReviewStatus && (
          <div className="flex items-center justify-between">
            <span className="font-medium">Follow-up check:</span>
            <span className={checkRepair.followUpReviewStatus === 'failed' ? 'text-red-600' : checkRepair.followUpReviewStatus === 'passed' ? 'text-green-600' : ''}>
              {checkRepair.followUpReviewStatus}
            </span>
          </div>
        )}

        {checkRepair.stopReason && (
          <div className="flex items-center justify-between">
            <span className="font-medium">Stop reason:</span>
            <span>{stopReasonLabels[checkRepair.stopReason] ?? checkRepair.stopReason}</span>
          </div>
        )}

        {checkRepair.unresolvedSummary && (
          <div className="mt-2 rounded bg-red-100 p-2">
            <div className="font-medium text-red-800 mb-1">Unresolved findings:</div>
            <div className="text-red-700 whitespace-pre-wrap">{checkRepair.unresolvedSummary}</div>
          </div>
        )}
      </div>

      {checkRepair.status === 'exhausted' && (
        <div className="pt-2 border-t border-red-200">
          <p className="text-xs text-red-600">
            Auto-fix will not continue automatically. You can rerun this stage after making code changes, or take over manually.
          </p>
        </div>
      )}
    </div>
  )
}

function IntegrateFailurePanel({ issue }: { issue: Issue }) {
  if (issue.workflowStage !== WorkflowStage.Integrate) return null
  if (issue.health !== IssueHealth.Blocked && issue.health !== IssueHealth.Interrupted) return null

  const blockedReason = issue.blockedReason ?? 'Integration step failed'

  let failingStep = 'unknown'
  let capabilityOrFiles = ''
  let requirementHeader = ''
  let mergeReason = ''
  let healthCommand = ''
  let healthSummary = ''
  let healthLogExcerpt = ''
  let nextAction = 'Review the failure above and take action to resolve the issue.'

  if (blockedReason) {
    if (blockedReason.includes('spec-sync') || blockedReason.includes('spec sync')) {
      failingStep = 'Sync main specs'
      nextAction = 'Check the OpenSpec delta specs for conflicts with existing requirements. Return to Build to fix spec issues.'
    } else if (blockedReason.includes('archive')) {
      failingStep = 'Archive OpenSpec change'
      nextAction = 'Check disk space and permissions. Retry the archive step or return to Build.'
    } else if (blockedReason.includes('merge') || blockedReason.includes('Merge')) {
      failingStep = 'Merge to target branch'
      nextAction = 'Resolve any merge conflicts and return to Build for re-check.'
    } else if (blockedReason.includes('health') || blockedReason.includes('final-health')) {
      failingStep = 'Run final integration health check'
      nextAction = 'Review the health check failure and fix the underlying issue. Return to Build for re-check.'
    }
  }

  return (
    <div className="rounded-lg border border-red-200 bg-red-50 p-4 space-y-3">
      <div className="flex items-center gap-2">
        <CrossIcon className="h-4 w-4 text-red-500" />
        <span className="text-sm font-semibold text-red-800">Integration Failed</span>
      </div>
      <div className="space-y-1.5">
        <div className="text-xs text-red-700">
          <span className="font-medium">Failing step:</span> {failingStep}
        </div>
        {capabilityOrFiles && (
          <div className="text-xs text-red-700">
            <span className="font-medium">Affected:</span> {capabilityOrFiles}
          </div>
        )}
        {(requirementHeader || mergeReason) && (
          <div className="text-xs text-red-700">
            {requirementHeader || mergeReason}
          </div>
        )}
        {(healthCommand || healthSummary) && (
          <div className="rounded bg-red-100 p-2 space-y-1">
            {healthCommand && (
              <div className="text-xs font-mono text-red-800">{healthCommand}</div>
            )}
            {healthSummary && (
              <div className="text-xs text-red-700">{healthSummary}</div>
            )}
            {healthLogExcerpt && (
              <div className="text-xs text-red-600 mt-1 font-mono whitespace-pre-wrap">{healthLogExcerpt}</div>
            )}
          </div>
        )}
        <div className="pt-1 border-t border-red-200">
          <p className="text-xs text-red-600">{nextAction}</p>
        </div>
      </div>
    </div>
  )
}

export function WorkflowView({ issue }: { issue: Issue }) {
  const isClosed = issue.status === IssueStatus.Cancelled
  const isCompleted = issue.status === IssueStatus.Done
  const isBacklog = issue.status === IssueStatus.Backlog
  const readOnly = isClosed
  const { data: timeline } = useWorkflowTimeline(issue.number, !isBacklog)
  const stageStateMap = useMemo(() => workflowTimelineToStageStateMap(timeline), [timeline])

  const getDefaultStage = useCallback((): WorkflowStage => {
    if (isBacklog) return WorkflowStage.Plan
    if (isCompleted) return WorkflowStage.Done
    const currentIdx = issue.workflowStage ? WORKFLOW_STAGES.indexOf(issue.workflowStage) : -1
    if (currentIdx >= 0) return WORKFLOW_STAGES[currentIdx]
    return WorkflowStage.Plan
  }, [issue.workflowStage, isBacklog, isCompleted])

  const [selectedStage, setSelectedStage] = useState<WorkflowStage>(getDefaultStage)

  useEffect(() => {
    setSelectedStage(getDefaultStage())
  }, [getDefaultStage])

  const [runningTaskStarts, setRunningTaskStarts] = useState<Map<string, number>>(new Map())
  const [liveElapsed, setLiveElapsed] = useState<Map<string, number>>(new Map())
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)

  useEffect(() => {
    const off = onAgentEvent('stage_task_update', (evt: AgentDetailEventMap['stage_task_update']) => {
      if (evt.issueId !== issue.id) return
      if (evt.status === 'started') {
        setRunningTaskStarts((prev) => {
          const next = new Map(prev)
          next.set(evt.taskId, Date.now())
          return next
        })
      } else if (evt.status === 'completed' || evt.status === 'failed') {
        setRunningTaskStarts((prev) => {
          const next = new Map(prev)
          next.delete(evt.taskId)
          return next
        })
        setLiveElapsed((prev) => {
          const next = new Map(prev)
          next.delete(evt.taskId)
          return next
        })
      }
    })
    return off
  }, [issue.id])

  useEffect(() => {
    if (runningTaskStarts.size === 0) {
      if (intervalRef.current) {
        clearInterval(intervalRef.current)
        intervalRef.current = null
      }
      return
    }
    if (intervalRef.current) return

    intervalRef.current = setInterval(() => {
      setLiveElapsed((prev) => {
        const next = new Map(prev)
        const now = Date.now()
        for (const [taskId, start] of runningTaskStarts) {
          next.set(taskId, now - start)
        }
        return next
      })
    }, 500)

    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current)
        intervalRef.current = null
      }
    }
  }, [runningTaskStarts])

  const runningDurations = useMemo(() => {
    const map = new Map<string, number>()
    for (const [taskId, elapsed] of liveElapsed) {
      for (const [stageKey, ss] of stageStateMap) {
        if (ss.tasks.some(t => t.taskId === taskId)) {
          const existing = map.get(stageKey) ?? 0
          map.set(stageKey, existing + elapsed)
          break
        }
      }
    }
    return map
  }, [liveElapsed, stageStateMap])

  const handleSelectStage = useCallback(
    (stage: WorkflowStage) => {
      if (readOnly) return
      setSelectedStage(stage)
    },
    [readOnly],
  )

  return (
    <div className="space-y-4">
      <StageBar
        stageStateMap={stageStateMap}
        issue={issue}
        selectedStage={selectedStage}
        onSelectStage={handleSelectStage}
        readOnly={readOnly}
        runningDurations={runningDurations}
      />

      {(isBacklog || issue.health === IssueHealth.Blocked || issue.health === IssueHealth.Interrupted) && (
        <SpecialStatePanel issue={issue} issueNumber={issue.number} readOnly={readOnly} />
      )}

      {!isBacklog && (
        <StepList
          stage={selectedStage}
          stageStateMap={stageStateMap}
          issue={issue}
          readOnly={readOnly}
          liveElapsedByTask={liveElapsed}
        />
      )}

      {issue.workflowStage === WorkflowStage.Integrate && (issue.health === IssueHealth.Blocked || issue.health === IssueHealth.Interrupted) && (
        <IntegrateFailurePanel issue={issue} />
      )}

    </div>
  )
}
