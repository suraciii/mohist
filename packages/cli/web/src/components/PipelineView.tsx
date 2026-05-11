import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api'
import { useIssueExecutions, useIssueStageState } from '../hooks/useQueries'
import { onAgentEvent } from '../lib/agent-events'
import { Stage, IssueStatus } from '../lib/types'
import type { Issue, StageExecution, StageTaskResult, StageTaskState, StageCheckState, StageStateRead, AgentDetailEventMap, OpenSpecSyncOutput, MergeReadinessOutput, IntegrationHealthGatePolicy } from '../lib/types'
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

const PIPELINE_STAGES = [Stage.Plan, Stage.Build, Stage.Check, Stage.Integrate, Stage.Done] as const
type PipelineStage = (typeof PIPELINE_STAGES)[number]

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  const m = Math.floor(ms / 60000)
  const s = Math.floor((ms % 60000) / 1000)
  return `${m}m ${s}s`
}

function getStageStatus(
  stage: PipelineStage,
  stageStateMap: Map<string, StageStateRead>,
  issue: Issue,
): 'pending' | 'running' | 'completed' | 'failed' | 'awaiting-approval' {
  const stageState = stageStateMap.get(stage)
  const stageOrder = PIPELINE_STAGES.indexOf(stage)
  const currentStageIdx = PIPELINE_STAGES.indexOf(issue.stage as PipelineStage)

  if (stageState) {
    if (stageState.status === 'running') return 'running'
    if (stageState.status === 'awaiting-approval') return 'awaiting-approval'
    if (stageState.status === 'passed') return 'completed'
    if (stageState.status === 'failed') return 'failed'
    if (stageState.status === 'skipped') return 'pending'
  }

  if (issue.stage === stage && !stageState) return 'running'

  if (issue.status === IssueStatus.Completed && stage === Stage.Done) return 'completed'

  if (currentStageIdx < 0 || stageOrder > currentStageIdx) return 'pending'

  return 'pending'
}

function getStageDuration(stage: PipelineStage, stageStateMap: Map<string, StageStateRead>): number | null {
  const stageState = stageStateMap.get(stage)
  if (!stageState || stageState.tasks.length === 0) return null
  const total = stageState.tasks.reduce((sum, t) => sum + (t.duration || 0), 0)
  return total > 0 ? total : null
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
  stage: PipelineStage
  status: string
  duration: number | null
  selected: boolean
  readOnly: boolean
  onClick: () => void
}) {
  const bgColor = selected ? 'bg-gray-50 border-gray-300' : 'bg-white border-gray-200'
  const stageLabel = stage.charAt(0).toUpperCase() + stage.slice(1)

  return (
    <button
      onClick={onClick}
      disabled={readOnly && status === 'pending'}
      className={`flex-1 min-w-0 rounded-lg border p-3 text-left transition-colors ${bgColor} ${
        !readOnly && status !== 'pending' ? 'cursor-pointer hover:bg-gray-100' : ''
      } ${status === 'pending' && !selected ? 'opacity-60' : ''}`}
    >
      <div className="flex items-center gap-2 mb-1">
        <StageStatusIcon status={status} />
        <span className="text-sm font-medium text-gray-900 truncate">{stageLabel}</span>
      </div>
      {status === 'completed' && duration != null && (
        <span className="text-xs text-gray-400 ml-7">{formatDuration(duration)}</span>
      )}
      {status === 'running' && duration != null && (
        <span className="text-xs text-blue-500 ml-7">{formatDuration(duration)}</span>
      )}
    </button>
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
  selectedStage: PipelineStage
  onSelectStage: (stage: PipelineStage) => void
  readOnly: boolean
  runningDurations: Map<string, number>
}) {
  return (
    <div className="flex items-stretch gap-2">
      {PIPELINE_STAGES.map((stage, idx) => {
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

function TaskItem({
  task,
  readOnly,
  liveElapsed,
}: {
  task: StageTaskState
  readOnly: boolean
  liveElapsed?: number | null
}) {
  const [expanded, setExpanded] = useState(false)
  const isPending = task.status === 'pending'
  const isRunning = task.status === 'running'
  const isFailed = task.status === 'failed'
  const taskOutput = task.output
  const hasOutput = taskOutput != null
  const canExpand = task.artifacts.length > 0 || isFailed || hasOutput

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

  return (
    <div
      className={`rounded-md border border-gray-200 overflow-hidden ${isPending ? 'opacity-50' : ''} ${isFailed ? 'border-red-200' : ''}`}
    >
      <button
        onClick={() => !readOnly && canExpand && setExpanded(!expanded)}
        disabled={readOnly}
        className="w-full flex items-center gap-2 px-3 py-2 text-left hover:bg-gray-50 transition-colors"
      >
        {icon}
        <span className="text-sm text-gray-900 flex-1 truncate">{task.title}</span>
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
      </button>
      {expanded && canExpand && (
        <div className="px-3 pb-2 border-t border-gray-100 bg-gray-50">
          <div className="mt-2 space-y-1">
            {task.artifacts.map((a) => (
              <div key={a} className="flex items-center gap-1.5 text-xs text-gray-500">
                <svg className="h-3 w-3 flex-shrink-0" viewBox="0 0 20 20" fill="currentColor">
                  <path d="M3 3.5A1.5 1.5 0 014.5 2h6.879a1.5 1.5 0 011.06.44l4.122 4.12A1.5 1.5 0 0117 7.622V16.5a1.5 1.5 0 01-1.5 1.5h-11A1.5 1.5 0 013 16.5v-13z" />
                </svg>
                <span className="font-mono truncate">{a}</span>
              </div>
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

function isHealthGateCheck(check: StageCheckState): boolean {
  const output = check.output as { kind?: string } | undefined
  return output?.kind === 'health-gate' || check.checkName.startsWith('health:')
}

function CheckItem({ check, attemptLabel }: { check: StageCheckState; attemptLabel?: string }) {
  const isPending = check.status === 'pending'
  const isFailed = check.status === 'failed' || check.status === 'error'
  const isHealthGate = isHealthGateCheck(check)
  const healthOutput = check.output as { command?: string; duration?: number; summary?: string; logExcerpt?: string; enabled?: boolean; exitCode?: number; timedOut?: boolean } | undefined

  let icon: React.ReactNode
  if (check.status === 'passed') {
    icon = <CheckmarkIcon className="h-4 w-4 text-green-500 flex-shrink-0" />
  } else if (isFailed) {
    icon = <CrossIcon className="h-4 w-4 text-red-500 flex-shrink-0" />
  } else if (check.status === 'running') {
    icon = <SpinnerIcon className="h-4 w-4 text-blue-500 animate-spin flex-shrink-0" />
  } else {
    icon = <EmptyCircleIcon className="h-4 w-4 text-gray-300 flex-shrink-0" />
  }

  const baseName = isHealthGate ? `Health Gate: ${check.checkName.replace('health:', '')}` : check.checkName
  const displayName = attemptLabel ? `${baseName} (${attemptLabel})` : baseName

  return (
    <div
      className={`flex items-center gap-2 px-3 py-2 rounded-md border ${isHealthGate && isFailed ? 'border-red-200 bg-red-50' : ''} ${isPending ? 'opacity-50' : ''}`}
    >
      {icon}
      <span className="text-sm text-gray-900 flex-1 truncate">{displayName}</span>
      {isFailed && check.message && (
        <span className="text-xs text-red-500 flex-shrink-0 truncate max-w-48">{check.message}</span>
      )}
      {isHealthGate && healthOutput && (
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

function InlineApproval({
  issueNumber,
  stage,
  readOnly,
  approvalOutput,
}: {
  issueNumber: number
  stage: PipelineStage
  readOnly: boolean
  approvalOutput?: Record<string, unknown>
}) {
  const queryClient = useQueryClient()
  const [feedback, setFeedback] = useState('')
  const [messageText, setMessageText] = useState('')
  const [showSendMessage, setShowSendMessage] = useState(false)
  const [reportModalOpen, setReportModalOpen] = useState(false)
  const [instructionsExpanded, setInstructionsExpanded] = useState(false)
  const [instructionsText, setInstructionsText] = useState('')
  const [notesExpanded, setNotesExpanded] = useState(false)
  const [notesText, setNotesText] = useState('')

  const review: ReviewOutput = useMemo(() => parseReviewOutput(approvalOutput), [approvalOutput])
  const classified = useMemo(() => classifyResult(review.result), [review.result])

  const approveMutation = useMutation({
    mutationFn: () => api.approveIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'executions'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'stage-state'] })
    },
  })

  const rejectMutation = useMutation({
    mutationFn: () => api.rejectIssue(issueNumber, { message: feedback.trim() || undefined }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'executions'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, 'stage-state'] })
      setFeedback('')
    },
  })

  const sendMessageMutation = useMutation({
    mutationFn: (message: string) => api.sendMessage(issueNumber, message),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      setMessageText('')
      setShowSendMessage(false)
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
      sendMessageMutation.mutate(`Please fix the following issues:\n\n${parts.join('\n\n')}`)
    } else if (review.reviewReport) {
      sendMessageMutation.mutate(`Please fix the following issues:\n\n${review.reviewReport}`)
    } else {
      sendMessageMutation.mutate('The review found issues that need to be addressed. Please review and fix all problems.')
    }
  }, [review, sendMessageMutation])

  const handleSendWithInstructions = useCallback(() => {
    if (!instructionsText.trim()) return
    sendMessageMutation.mutate(instructionsText.trim())
  }, [instructionsText, sendMessageMutation])

  const handleSendBackWithNotes = useCallback(() => {
    if (!notesText.trim()) return
    sendMessageMutation.mutate(notesText.trim())
  }, [notesText, sendMessageMutation])

  const handleApproveAnyway = useCallback(() => {
    approveMutation.mutate()
  }, [approveMutation])

  const handleViewChanges = useCallback(() => {
    document.getElementById('changes-panel')?.scrollIntoView({ behavior: 'smooth' })
  }, [])

  const getApproveLabel = () => {
    if (stage === Stage.Plan) return 'Approve & Start Build'
    if (stage === Stage.Check) return 'Approve & Queue Merge'
    return 'Approve'
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
        {stage === Stage.Plan
          ? 'Review the design proposal and approve to start implementation.'
          : stage === Stage.Check
            ? 'Review the check results and approve to queue for merge.'
            : `Review the ${stage} stage output and approve to continue, or send back with feedback.`}
      </p>

      {hasApprovalOutput && (
        <ReviewSummary output={approvalOutput} />
      )}

      {hasApprovalOutput && (
        <div className="flex gap-4 text-xs">
          <button
            onClick={() => setReportModalOpen(true)}
            className="text-blue-600 hover:text-blue-800 transition-colors"
          >
            View Full Report
          </button>
          <button
            onClick={handleViewChanges}
            className="text-blue-600 hover:text-blue-800 transition-colors"
          >
            View Changes
          </button>
        </div>
      )}

      {!hasApprovalOutput && (
        <div className="space-y-2">
          <div className="flex gap-2">
            <button
              onClick={() => approveMutation.mutate()}
              disabled={approveMutation.isPending}
              className="flex-1 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
            >
              {approveMutation.isPending ? 'Approving...' : getApproveLabel()}
            </button>
            <button
              onClick={() => rejectMutation.mutate()}
              disabled={rejectMutation.isPending}
              className="flex-1 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
            >
              {rejectMutation.isPending ? 'Sending...' : 'Send back'}
            </button>
          </div>
          <textarea
            value={feedback}
            onChange={(e) => setFeedback(e.target.value)}
            placeholder="Optional feedback..."
            rows={2}
            className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
          />
        </div>
      )}

      {hasApprovalOutput && classified === 'PASS' && (
        <div className="space-y-2">
          <button
            onClick={() => approveMutation.mutate()}
            disabled={approveMutation.isPending}
            className="w-full rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50 transition-colors"
          >
            {approveMutation.isPending ? 'Approving...' : getApproveLabel()}
          </button>
        </div>
      )}

      {hasApprovalOutput && classified === 'FAIL' && (
        <div className="space-y-2">
          <button
            onClick={handleSendBackForFixes}
            disabled={sendMessageMutation.isPending}
            className="w-full rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
          >
            {sendMessageMutation.isPending ? 'Sending...' : 'Send back for fixes'}
          </button>

          <div>
            <button
              onClick={() => setInstructionsExpanded(!instructionsExpanded)}
              className="text-sm text-gray-600 hover:text-gray-800 transition-colors"
            >
              {instructionsExpanded ? '▾' : '▸'} Add instructions...
            </button>
            {instructionsExpanded && (
              <div className="mt-2 space-y-2">
                <textarea
                  value={instructionsText}
                  onChange={(e) => setInstructionsText(e.target.value)}
                  placeholder="Add your instructions for the fix..."
                  rows={3}
                  className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
                />
                <button
                  onClick={handleSendWithInstructions}
                  disabled={!instructionsText.trim() || sendMessageMutation.isPending}
                  className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                >
                  {sendMessageMutation.isPending ? 'Sending...' : 'Send with instructions'}
                </button>
              </div>
            )}
          </div>

          <button
            onClick={handleApproveAnyway}
            disabled={approveMutation.isPending}
            className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50 disabled:opacity-50 transition-colors"
          >
            {approveMutation.isPending ? 'Approving...' : 'Approve anyway'}
          </button>
        </div>
      )}

      {hasApprovalOutput && classified === 'UNKNOWN' && (
        <div className="space-y-2">
          <button
            onClick={() => approveMutation.mutate()}
            disabled={approveMutation.isPending}
            className="w-full rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            {approveMutation.isPending ? 'Approving...' : getApproveLabel()}
          </button>

          <div>
            <button
              onClick={() => setNotesExpanded(!notesExpanded)}
              className="text-sm text-gray-600 hover:text-gray-800 transition-colors"
            >
              {notesExpanded ? '▾' : '▸'} Send back with notes...
            </button>
            {notesExpanded && (
              <div className="mt-2 space-y-2">
                <textarea
                  value={notesText}
                  onChange={(e) => setNotesText(e.target.value)}
                  placeholder="Describe what needs to be changed..."
                  rows={3}
                  className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
                />
                <button
                  onClick={handleSendBackWithNotes}
                  disabled={!notesText.trim() || sendMessageMutation.isPending}
                  className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                >
                  {sendMessageMutation.isPending ? 'Sending...' : 'Send'}
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {(approveMutation.error || rejectMutation.error || sendMessageMutation.error) && (
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          {approveMutation.error?.message || rejectMutation.error?.message || sendMessageMutation.error?.message}
        </div>
      )}

      {!hasApprovalOutput && (
        <div className="border-t border-amber-200 pt-3">
          {!showSendMessage ? (
            <button
              onClick={() => setShowSendMessage(true)}
              className="text-xs text-blue-600 hover:text-blue-800 transition-colors"
            >
              Send a message to the agent instead
            </button>
          ) : (
            <div className="space-y-2">
              <h4 className="text-xs font-semibold text-blue-800">Send Message</h4>
              <p className="text-xs text-blue-600">
                Send a free-text message to the agent. The agent will decide the next step based on your message.
              </p>
              <textarea
                value={messageText}
                onChange={(e) => setMessageText(e.target.value)}
                placeholder="Type a message to the agent..."
                rows={2}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 resize-none"
              />
              <div className="flex items-center justify-between">
                {sendMessageMutation.error && (
                  <span className="text-xs text-red-500">
                    {sendMessageMutation.error.message}
                  </span>
                )}
                <div className="ml-auto flex gap-2">
                  <button
                    onClick={() => setShowSendMessage(false)}
                    className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={() => sendMessageMutation.mutate(messageText)}
                    disabled={!messageText.trim() || sendMessageMutation.isPending}
                    className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
                  >
                    {sendMessageMutation.isPending ? 'Sending...' : 'Send'}
                  </button>
                </div>
              </div>
            </div>
          )}
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
  stage: PipelineStage
  stageStateMap: Map<string, StageStateRead>
  issue: Issue
  readOnly: boolean
  liveElapsedByTask: Map<string, number>
}) {
  const stageState = stageStateMap.get(stage)
  const taskResults: StageTaskState[] = stageState?.tasks ?? []
  const checkResults: StageCheckState[] = stageState?.checks ?? []

  const healthGateChecks = checkResults.filter(c =>
    c.checkName.startsWith('health:') || (c.output && (c.output as Record<string, unknown>).kind === 'health-gate')
  )
  const failedHealthGates = healthGateChecks.filter(c => c.status === 'failed' || c.status === 'error')

  const isAwaitingApproval =
    failedHealthGates.length === 0 &&
    issue.approvalState?.status === 'awaiting' &&
    issue.stage === stage &&
    (issue.status === IssueStatus.Active || issue.status === IssueStatus.Blocked)

  return (
    <div className="space-y-4">
      {stage !== Stage.Done && (
        <div>
          <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wide mb-2">Tasks</h3>
          <div className="space-y-1.5">
            {taskResults.length > 0 ? (
              taskResults.map((task) => (
                <TaskItem
                  key={task.taskId}
                  task={task}
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

      {checkResults.length > 0 && stage !== Stage.Done && (
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

  const startMutation = useMutation({
    mutationFn: () => api.startIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const reopenMutation = useMutation({
    mutationFn: () => api.reopenIssue(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  if (readOnly) return null

  if (issue.stage === Stage.Backlog || issue.stage === Stage.Draft) {
    return (
      <div className="flex justify-center py-4">
        <button
          onClick={() => startMutation.mutate()}
          disabled={startMutation.isPending}
          className="rounded-md bg-blue-600 px-6 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
        >
          {startMutation.isPending ? 'Starting...' : 'Start'}
        </button>
      </div>
    )
  }

  if (issue.status === IssueStatus.Blocked) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-4 space-y-2">
        <div className="flex items-center gap-2">
          <CrossIcon className="h-4 w-4 text-red-500" />
          <span className="text-sm font-semibold text-red-800">Blocked</span>
        </div>
        {issue.blockedReason && (
          <p className="text-sm text-red-600">{issue.blockedReason}</p>
        )}
      </div>
    )
  }

  if (issue.status === IssueStatus.Interrupted) {
    return (
      <div className="rounded-lg border border-orange-200 bg-orange-50 p-4 space-y-3">
        <div className="flex items-center gap-2">
          <InterruptedIcon />
          <span className="text-sm font-semibold text-orange-800">Pipeline Interrupted</span>
        </div>
        <p className="text-xs text-orange-600">
          The pipeline was interrupted. Click &quot;Resume&quot; to continue from where it left off.
        </p>
        <button
          onClick={() => reopenMutation.mutate()}
          disabled={reopenMutation.isPending}
          className="rounded-md bg-orange-500 px-4 py-2 text-sm font-medium text-white hover:bg-orange-600 disabled:opacity-50 transition-colors"
        >
          {reopenMutation.isPending ? 'Resuming...' : 'Resume'}
        </button>
      </div>
    )
  }

return null
}

function DoneEvidencePanel({ executions }: { executions: StageExecution[] }) {
  const integrateExecution = executions.filter(e => e.stage === Stage.Integrate).at(-1)
  const checkExecution = executions.filter(e => e.stage === Stage.Check).at(-1)

  const checkOutput = checkExecution?.checkResults
    ?.find(c => c.name === 'openspec-sync-dry-run')?.output as OpenSpecSyncOutput | undefined
  const mergeReadinessOutput = (checkExecution?.checkResults
    ?.find(c => c.name === 'merge-ready' || c.name === 'merge-readiness')?.output as MergeReadinessOutput | undefined)
  const healthGatePolicyOutput = checkExecution?.checkResults
    ?.find(c => c.name === 'integration-health-gate-preview')?.output as IntegrationHealthGatePolicy | undefined

  const specSyncResult = integrateExecution?.taskResults?.find(t => t.taskId === 'integrate:spec-sync')
  const archiveResult = integrateExecution?.taskResults?.find(t => t.taskId === 'integrate:archive-change')
  const mergeResult = integrateExecution?.taskResults?.find(t => t.taskId === 'integrate:merge')
  const finalHealthResult = integrateExecution?.taskResults?.find(t => t.taskId === 'final-health')

  const specSyncOutput = specSyncResult ? (specSyncResult as StageTaskResult).output as OpenSpecSyncOutput | undefined : undefined
  const archiveOutput = archiveResult ? (archiveResult as StageTaskResult).output as { archivePath?: string } | undefined : undefined
  const mergeOutput = mergeResult ? (mergeResult as StageTaskResult).output as { mergedSha?: string; targetBranch?: string; baseSha?: string; headSha?: string } | undefined : undefined
  const healthOutput = finalHealthResult ? (finalHealthResult as StageTaskResult).output as { command?: string; duration?: number; summary?: string; passed?: boolean; exitCode?: number } | undefined : undefined

  if (!integrateExecution && !checkExecution) return null

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 space-y-4">
      <h3 className="text-sm font-semibold text-gray-700">Integration Evidence</h3>

      {(checkOutput || mergeReadinessOutput || healthGatePolicyOutput) && (
        <div className="space-y-3">
          <h4 className="text-xs font-medium text-gray-500 uppercase tracking-wide">Check Readiness</h4>
          {checkOutput && (
            <div className="rounded-md bg-blue-50 border border-blue-100 p-3 space-y-1.5">
              <div className="text-xs font-medium text-blue-800">Spec Sync Dry-Run</div>
              <div className="text-xs text-blue-700">
                {checkOutput.valid ? (
                  <>
                    {checkOutput.capabilities.length > 0 && (
                      <span>Capabilities: {checkOutput.capabilities.join(', ')}</span>
                    )}
                    {checkOutput.targetFiles.length > 0 && (
                      <span className="block">Target files: {checkOutput.targetFiles.length}</span>
                    )}
                    {checkOutput.counts && (
                      <span className="block">
                        {checkOutput.counts.added} added, {checkOutput.counts.modified} modified,{' '}
                        {checkOutput.counts.removed} removed, {checkOutput.counts.renamed} renamed
                      </span>
                    )}
                    {checkOutput.conflicts && checkOutput.conflicts.length > 0 && (
                      <span className="block text-red-600">
                        Conflicts: {checkOutput.conflicts.length}
                      </span>
                    )}
                  </>
                ) : (
                  <span className="text-red-600">Dry-run failed</span>
                )}
              </div>
            </div>
          )}
          {mergeReadinessOutput && (
            <div className="rounded-md bg-purple-50 border border-purple-100 p-3 space-y-1.5">
              <div className="text-xs font-medium text-purple-800">Merge Readiness</div>
              <div className="text-xs text-purple-700">
                <span>Fast-forward: {mergeReadinessOutput.canFastForward ? 'Yes' : 'No'}</span>
                <span className="block">Clean rebase: {mergeReadinessOutput.cleanRebaseFeasible ? 'Yes' : 'No'}</span>
                {mergeReadinessOutput.conflictFiles && mergeReadinessOutput.conflictFiles.length > 0 && (
                  <span className="block text-red-600">
                    Conflicts: {mergeReadinessOutput.conflictFiles.join(', ')}
                  </span>
                )}
              </div>
            </div>
          )}
          {healthGatePolicyOutput && (
            <div className="rounded-md bg-green-50 border border-green-100 p-3 space-y-1.5">
              <div className="text-xs font-medium text-green-800">Final Health Gate Policy</div>
              <div className="text-xs text-green-700">
                <span className="font-mono">{healthGatePolicyOutput.command}</span>
                <span className="block">Timeout: {healthGatePolicyOutput.timeout}s</span>
                <span className="block">Enabled: {healthGatePolicyOutput.enabled ? 'Yes' : 'No'}</span>
              </div>
            </div>
          )}
        </div>
      )}

      {integrateExecution && (
        <div className="space-y-3">
          <h4 className="text-xs font-medium text-gray-500 uppercase tracking-wide">Integrate Steps</h4>
          {specSyncResult && specSyncResult.status === 'completed' && (
            <div className="rounded-md bg-gray-50 border border-gray-100 p-3 space-y-1.5">
              <div className="flex items-center gap-2">
                <CheckmarkIcon className="h-3.5 w-3.5 text-green-500" />
                <span className="text-xs font-medium text-gray-800">Spec Sync</span>
              </div>
              {specSyncOutput && (
                <div className="text-xs text-gray-600 ml-5">
                  {specSyncOutput.capabilities.length > 0 && (
                    <span>Capabilities: {specSyncOutput.capabilities.join(', ')}</span>
                  )}
                </div>
              )}
            </div>
          )}
          {archiveResult && archiveResult.status === 'completed' && (
            <div className="rounded-md bg-gray-50 border border-gray-100 p-3 space-y-1.5">
              <div className="flex items-center gap-2">
                <CheckmarkIcon className="h-3.5 w-3.5 text-green-500" />
                <span className="text-xs font-medium text-gray-800">Archive OpenSpec Change</span>
              </div>
              {archiveOutput?.archivePath && (
                <div className="text-xs text-gray-600 ml-5 font-mono">{archiveOutput.archivePath}</div>
              )}
            </div>
          )}
          {mergeResult && mergeResult.status === 'completed' && (
            <div className="rounded-md bg-gray-50 border border-gray-100 p-3 space-y-1.5">
              <div className="flex items-center gap-2">
                <CheckmarkIcon className="h-3.5 w-3.5 text-green-500" />
                <span className="text-xs font-medium text-gray-800">Merge to Target Branch</span>
              </div>
              {mergeOutput && (
                <div className="text-xs text-gray-600 ml-5 font-mono">
                  {mergeOutput.targetBranch}: {mergeOutput.baseSha?.slice(0, 7)} → {mergeOutput.headSha?.slice(0, 7)}
                  {mergeOutput.mergedSha && ` → ${mergeOutput.mergedSha.slice(0, 7)}`}
                </div>
              )}
            </div>
          )}
          {finalHealthResult && finalHealthResult.status === 'completed' && (
            <div className="rounded-md bg-gray-50 border border-gray-100 p-3 space-y-1.5">
              <div className="flex items-center gap-2">
                <CheckmarkIcon className="h-3.5 w-3.5 text-green-500" />
                <span className="text-xs font-medium text-gray-800">Final Health Gate</span>
              </div>
              {healthOutput && (
                <div className="text-xs text-gray-600 ml-5">
                  {healthOutput.summary}
                  <span className="block font-mono">{healthOutput.command}</span>
                </div>
              )}
            </div>
          )}
          {finalHealthResult && finalHealthResult.status === 'failed' && (
            <div className="rounded-md bg-red-50 border border-red-200 p-3 space-y-1.5">
              <div className="flex items-center gap-2">
                <CrossIcon className="h-3.5 w-3.5 text-red-500" />
                <span className="text-xs font-medium text-red-800">Final Health Gate Failed</span>
              </div>
              {healthOutput && (
                <div className="text-xs text-red-600 ml-5">
                  {healthOutput.summary}
                  <span className="block font-mono">{healthOutput.command}</span>
                  {healthOutput.exitCode != null && <span className="block">Exit code: {healthOutput.exitCode}</span>}
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function IntegrateFailurePanel({ issue }: { issue: Issue }) {
  if (issue.stage !== Stage.Integrate) return null
  if (issue.status !== IssueStatus.Blocked && issue.status !== IssueStatus.Interrupted) return null

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
    } else if (blockedReason.includes('health') || blockedReason.includes('final-health') || blockedReason.includes('health gate')) {
      failingStep = 'Run final integration health gate'
      nextAction = 'Review the health gate failure and fix the underlying issue. Return to Build for re-check.'
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

export function PipelineView({ issue }: { issue: Issue }) {
  const { data: stageStateData } = useIssueStageState(issue.number)
  const { data: executions = [] } = useIssueExecutions(issue.number)

  const stageStateMap = useMemo(() => {
    const map = new Map<string, StageStateRead>()
    if (stageStateData?.stages) {
      for (const ss of stageStateData.stages) {
        map.set(ss.stage, ss)
      }
    }
    return map
  }, [stageStateData])

  const isClosed = issue.status === IssueStatus.Closed
  const isCompleted = issue.status === IssueStatus.Completed
  const isBacklog = issue.stage === Stage.Backlog || issue.stage === Stage.Draft
  const readOnly = isClosed

  const getDefaultStage = useCallback((): PipelineStage => {
    if (isBacklog) return Stage.Plan
    if (isCompleted) return Stage.Done
    const currentIdx = PIPELINE_STAGES.indexOf(issue.stage as PipelineStage)
    if (currentIdx >= 0) return PIPELINE_STAGES[currentIdx]
    return Stage.Plan
  }, [issue.stage, isBacklog, isCompleted])

  const [selectedStage, setSelectedStage] = useState<PipelineStage>(getDefaultStage)

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
    (stage: PipelineStage) => {
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

      {(isBacklog || issue.status === IssueStatus.Blocked || issue.status === IssueStatus.Interrupted) && (
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

      {isCompleted && (
        <DoneEvidencePanel executions={executions} />
      )}

      {issue.stage === Stage.Integrate && (issue.status === IssueStatus.Blocked || issue.status === IssueStatus.Interrupted) && (
        <IntegrateFailurePanel issue={issue} />
      )}
    </div>
  )
}
