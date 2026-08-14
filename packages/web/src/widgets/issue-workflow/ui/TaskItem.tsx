import { useState, useEffect, useCallback } from 'react'
import { Link } from 'react-router-dom'
import { ChevronDownIcon, MessageSquareIcon } from 'lucide-react'
import { getFileContent } from '../../../entities/issue'
import type { StageTaskState, WorkflowArtifactSummary } from '../../../entities/issue'
import { useProject, useProjectPath } from '../../../entities/project'
import { ArtifactContentViewer, type ArtifactContentHook } from './ArtifactContentViewer'
import { getDeliveryFailureGuidance } from '../../../shared/lib/delivery-failure'
import { formatClock, formatDuration, formatOriginLabel, formatOriginTitle } from './format'
import { CheckmarkIcon, CrossIcon, EmptyCircleIcon, HourglassIcon, SpinnerIcon } from './StageStatusIcons'
import { DeliveryFailureBanner } from './failure-panels'
import { TaskLogPanel, useDefaultTaskLogData, type TaskLogDataHook, type WorkflowRunSessionsHook } from './TaskLogPanel'

function TaskLifecycleTime({ task }: { task: StageTaskState }) {
  const startedAt = task.startedAt
  if (!startedAt) return null

  const startClock = formatClock(startedAt)

  if (task.status === 'running') {
    return <RunningElapsed startClock={startClock} startedAt={startedAt} />
  }

  const completedAt = task.completedAt
  if (!completedAt) {
    return (
      <span className="text-xs text-muted-foreground/70 flex-shrink-0">{startClock}</span>
    )
  }

  const endClock = formatClock(completedAt)
  const dur = task.duration > 0 ? ` · ${formatDuration(task.duration)}` : ''
  return (
    <span className="text-xs text-muted-foreground/70 flex-shrink-0" title={`Started ${startClock}, ended ${endClock}`}>
      {startClock}→{endClock}{dur}
    </span>
  )
}

function RunningElapsed({ startClock, startedAt }: { startClock: string; startedAt: string }) {
  const [elapsedMs, setElapsedMs] = useState(() => Date.now() - new Date(startedAt).getTime())

  useEffect(() => {
    const id = setInterval(() => {
      setElapsedMs(Date.now() - new Date(startedAt).getTime())
    }, 1000)
    return () => clearInterval(id)
  }, [startedAt])

  return (
    <span className="text-xs text-muted-foreground/70 flex-shrink-0" title={`Started at ${startClock}`}>
      {startClock} · {formatDuration(elapsedMs)}
    </span>
  )
}

function RequiredFileEntry({
  rf,
  issueNumber,
  fileContentFn,
}: {
  rf: { path: string; source: string; canFetchContent: boolean; markers?: string[] }
  issueNumber: number
  fileContentFn: typeof getFileContent
}) {
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
        fileContentFn(issueNumber, rf.path, projectId)
          .then((resp) => setContent(resp.head || resp.base))
          .catch(() => setError(true))
          .finally(() => setLoading(false))
      }
    }
  }, [open, content, loading, rf.canFetchContent, rf.path, issueNumber, projectId, fileContentFn])

  return (
    <div className="text-xs">
      <button
        onClick={handleToggle}
        disabled={!rf.canFetchContent}
        className="flex items-center gap-1.5 text-muted-foreground hover:text-foreground disabled:opacity-50 transition-colors"
      >
        <svg className="h-3 w-3 flex-shrink-0 text-info" viewBox="0 0 20 20" fill="currentColor">
          <path d="M3 3.5A1.5 1.5 0 014.5 2h6.879a1.5 1.5 0 011.06.44l4.122 4.12A1.5 1.5 0 0117 7.622V16.5a1.5 1.5 0 01-1.5 1.5h-11A1.5 1.5 0 013 16.5v-13z" />
        </svg>
        <span className="font-mono truncate">{rf.path}</span>
        {rf.source === 'task-expect' && <span className="text-[10px] text-info/80 flex-shrink-0">expect</span>}
      </button>
      {open && (
        <div className="mt-1 rounded bg-muted p-2 max-h-60 overflow-auto">
          {loading && <span className="text-muted-foreground">loading...</span>}
          {error && <span className="text-danger">File content unavailable</span>}
          {content && <pre className="whitespace-pre-wrap break-words font-mono text-xs text-muted-foreground">{content}</pre>}
        </div>
      )}
    </div>
  )
}

function TaskSessionChip({ sessionName, sessionId }: { sessionName: string; sessionId?: string }) {
  const toProjectPath = useProjectPath()
  const transcriptPath = sessionId
    ? toProjectPath(`/sessions/${encodeURIComponent(sessionId)}`)
    : null

  if (!transcriptPath) return <span className="text-muted-foreground">{sessionName}</span>
  return (
    <Link
      to={transcriptPath}
      onClick={(event) => event.stopPropagation()}
      className="inline-flex items-center gap-1 rounded border border-info-border bg-info-subtle px-1.5 py-0.5 text-[11px] font-medium text-info hover:border-info/50 shrink-0"
      title={`Open ${sessionName} transcript`}
    >
      <MessageSquareIcon className="h-3 w-3" aria-hidden="true" />
      <span>{sessionName}</span>
    </Link>
  )
}

function TaskArtifactSummaryChip({
  summary,
  onClick,
}: {
  summary: WorkflowArtifactSummary
  onClick: () => void
}) {
  const isDirectory = summary.kind === 'directory'
  const handleClick = (event: React.MouseEvent<HTMLButtonElement>) => {
    event.stopPropagation()
    onClick()
  }
  return (
    <button
      type="button"
      onClick={handleClick}
      className="inline-flex items-center gap-1 text-[11px] px-2 py-0.5 rounded-full bg-info-subtle text-info hover:bg-info-subtle/80 transition-colors cursor-pointer"
      title={`Open recorded ${summary.path}`}
    >
      {isDirectory ? (
        <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
          <path d="M4 3a2 2 0 00-2 2v10a2 2 0 002 2h12a2 2 0 002-2V7a2 2 0 00-2-2h-3.586l-1.707-1.707A1 1 0 009.586 3H4z" />
        </svg>
      ) : (
        <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
          <path d="M3 3.5A1.5 1.5 0 014.5 2h6.879a1.5 1.5 0 011.06.44l4.122 4.12A1.5 1.5 0 0117 7.622V16.5a1.5 1.5 0 01-1.5 1.5h-11A1.5 1.5 0 013 16.5v-13z" />
        </svg>
      )}
      <span className="font-mono truncate">{summary.path}</span>
    </button>
  )
}

function isDeliveryFailureTask(task: StageTaskState): boolean {
  const uses = task.origin?.uses
  if (typeof uses !== 'string') {
    return typeof task.taskId === 'string' && (
      task.taskId.startsWith('integrate:prepare') ||
      task.taskId.startsWith('integrate:publish') ||
      task.taskId.startsWith('integrate:open-pr') ||
      task.taskId.startsWith('integrate:merge-pr') ||
      task.taskId.startsWith('recover:open-pr') ||
      task.taskId.startsWith('recover:merge-pr')
    )
  }
  return (
    uses === 'mohist/prepare' ||
    uses === 'mohist/publish' ||
    uses === 'mohist/publish-via-pr' ||
    uses === 'mohist/create-pull-request' ||
    uses === 'mohist/merge-pull-request'
  )
}

export function TaskItem({
  task,
  issueNumber,
  workflowRunId,
  artifactContentHook,
  fileContentFn = getFileContent,
  taskLogHook,
  workflowSessionsHook,
}: {
  task: StageTaskState
  issueNumber: number
  workflowRunId: string | null
  artifactContentHook?: ArtifactContentHook
  fileContentFn?: typeof getFileContent
  taskLogHook?: TaskLogDataHook
  workflowSessionsHook?: WorkflowRunSessionsHook
}) {
  const [expanded, setExpanded] = useState(false)
  const [selectedArtifact, setSelectedArtifact] = useState<WorkflowArtifactSummary | null>(null)
  const { projectId } = useProject()
  const isPending = task.status === 'pending'
  const isRunning = task.status === 'running'
  const isFailed = task.status === 'failed'
  const taskOutput = task.output
  const hasOutput = taskOutput != null
  const hasRequiredFiles = (task.requiredFiles?.length ?? 0) > 0
  const artifactSummaries = task.artifactSummaries ?? []
  const hasArtifacts = artifactSummaries.length > 0
  const isDeliveryTask = isDeliveryFailureTask(task)
  const taskReason = task.error?.message ?? (typeof task.reason === 'string' ? task.reason : null)
  const deliveryFailure = isFailed && isDeliveryTask
    ? getDeliveryFailureGuidance(task.error?.code)
    : null
  const resolvedTaskLogHook = taskLogHook ?? useDefaultTaskLogData
  const taskLogResult = resolvedTaskLogHook({
    issueNumber,
    taskId: task.taskId,
    projectId,
    workflowRunId,
    enabled: task.taskId.trim().length > 0 && task.status !== 'pending',
  })
  const hasLogs = task.taskId.trim().length > 0
    && (isRunning || (taskLogResult.data?.lines.length ?? 0) > 0)
  const canExpand = hasLogs || hasArtifacts || hasRequiredFiles || isFailed || hasOutput || deliveryFailure != null

  let icon: React.ReactNode
  if (task.status === 'completed') {
    icon = <CheckmarkIcon className="h-4 w-4 text-success flex-shrink-0" />
  } else if (isFailed) {
    icon = <CrossIcon className="h-4 w-4 text-danger flex-shrink-0" />
  } else if (task.status === 'blocked') {
    icon = <HourglassIcon className="h-4 w-4 text-warning flex-shrink-0" />
  } else if (isRunning) {
    icon = <SpinnerIcon className="h-4 w-4 text-info animate-spin flex-shrink-0" />
  } else {
    icon = <EmptyCircleIcon className="h-4 w-4 text-muted-foreground/40 flex-shrink-0" />
  }

  const hasReason = taskReason != null
  const originLabel = formatOriginLabel(task.origin)
  const originTitle = formatOriginTitle(task.origin)
  const sessionName = task.sessionName?.trim()
  const workflowSessions = workflowSessionsHook?.(workflowRunId).sessions ?? []
  const sessionId = sessionName
    ? workflowSessions.find((session) => session.sessionName === sessionName)?.id
    : undefined
  const detailsId = `task-details-${issueNumber}-${encodeURIComponent(task.taskId)}`

  const primaryContent = (
    <>
      {icon}
      <span className="min-w-0 flex-1 whitespace-normal break-words text-sm font-medium text-card-foreground">
        {task.title}
      </span>
      {isFailed && <span className="shrink-0 text-xs text-danger">failed</span>}
      {canExpand && (
        <ChevronDownIcon
          className={`size-4 shrink-0 text-muted-foreground transition-transform ${expanded ? 'rotate-180' : ''}`}
          aria-hidden="true"
        />
      )}
    </>
  )

  return (
    <div
      className={`rounded-md border overflow-hidden ${isPending ? 'opacity-50' : ''} ${isFailed ? 'border-danger-border bg-danger-subtle/40' : 'border-border bg-card'}`}
      data-testid="workflow-task-item"
      data-task-title={task.title}
    >
      {canExpand ? (
        <button
          type="button"
          onClick={() => setExpanded((value) => !value)}
          aria-expanded={expanded}
          aria-controls={detailsId}
          className="flex w-full min-w-0 items-start gap-2 px-3 py-2 text-left transition-colors hover:bg-muted"
        >
          {primaryContent}
        </button>
      ) : (
        <div className="flex min-w-0 items-start gap-2 px-3 py-2">
          {primaryContent}
        </div>
      )}
      {(task.status === 'completed' && hasArtifacts) || hasReason || originLabel || sessionName || task.startedAt || task.attempts > 1 ? (
        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 px-3 pb-2 pl-9 text-xs text-muted-foreground" data-testid="workflow-task-metadata">
          {task.status === 'completed' && artifactSummaries.map((summary) => (
            <TaskArtifactSummaryChip
              key={summary.artifactId}
              summary={summary}
              onClick={() => setSelectedArtifact(summary)}
            />
          ))}
          {hasReason && <span className="text-warning" title={taskReason ?? undefined}>reason</span>}
          {originLabel && <span className="break-all font-mono text-[11px]" title={originTitle}>{originLabel}</span>}
           {sessionName && <TaskSessionChip sessionName={sessionName} sessionId={sessionId} />}

          {task.attempts > 1 && <span>{task.attempts} attempts</span>}
          <TaskLifecycleTime task={task} />
        </div>
      ) : null}
      {expanded && canExpand && (
        <div id={detailsId} className="px-3 pb-2 border-t bg-muted" data-testid="workflow-task-details">
          <div className="mt-2 space-y-2">
            {deliveryFailure && (
              <DeliveryFailureBanner
                failureKind={deliveryFailure.failureKind}
                label={deliveryFailure.label}
                nextAction={deliveryFailure.nextAction}
              />
            )}
            {hasReason && (
              <div className="text-xs text-warning bg-warning-subtle rounded px-2 py-1">
                {taskReason}
              </div>
            )}
            {hasArtifacts && (
              <div className="space-y-1">
                <div className="text-[10px] uppercase tracking-wide text-muted-foreground font-semibold">Artifacts</div>
                <div className="flex flex-wrap gap-1.5">
                  {artifactSummaries.map((summary) => (
                    <TaskArtifactSummaryChip
                      key={summary.artifactId}
                      summary={summary}
                      onClick={() => setSelectedArtifact(summary)}
                    />
                  ))}
                </div>
              </div>
            )}
            {task.requiredFiles?.map((rf) => (
              <RequiredFileEntry
                key={rf.path}
                rf={rf}
                issueNumber={issueNumber}
                fileContentFn={fileContentFn}
              />
            ))}
            {hasOutput && (
              <pre className="text-xs text-muted-foreground whitespace-pre-wrap break-words font-mono bg-muted rounded p-2 max-h-40 overflow-auto">
                {JSON.stringify(taskOutput, null, 2)}
              </pre>
            )}
            {isFailed && !hasReason && !hasOutput && <p className="text-xs text-danger">Task failed</p>}
            {hasLogs && (
              <TaskLogPanel
                issueNumber={issueNumber}
                taskId={task.taskId}
                workflowRunId={workflowRunId}
                taskStatus={task.status}
                sessionName={task.sessionName ?? null}
                origin={task.origin ?? null}
                classification={task.classification ?? null}
                taskLogHook={() => taskLogResult}
                workflowSessionsHook={workflowSessionsHook}
              />
            )}
          </div>
        </div>
      )}
      {selectedArtifact && (
        <ArtifactContentViewer
          issueNumber={issueNumber}
          artifactId={selectedArtifact.artifactId}
          path={selectedArtifact.path}
          size={selectedArtifact.size}
          artifactKind={selectedArtifact.kind}
          open={selectedArtifact !== null}
          contentHook={artifactContentHook}
          onOpenChange={(open) => {
            if (!open) setSelectedArtifact(null)
          }}
        />
      )}
    </div>
  )
}
