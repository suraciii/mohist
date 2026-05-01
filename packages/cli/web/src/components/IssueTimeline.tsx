import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useIssueTimeline } from '../hooks/useIssueTimeline'
import type {
  TimelineNode,
  TimelineStageNode,
  TimelineCreatedNode,
  TimelineApprovedNode,
  TimelineRound,
  TimelineTask,
  TimelineStageStatus,
} from '../hooks/useIssueTimeline'

function formatDuration(ms: number | null): string {
  if (ms == null || ms < 0) return ''
  const totalSec = Math.floor(ms / 1000)
  if (totalSec < 60) return `${totalSec}s`
  const min = Math.floor(totalSec / 60)
  const sec = totalSec % 60
  if (min < 60) return `${min}m ${String(sec).padStart(2, '0')}s`
  const hr = Math.floor(min / 60)
  const remMin = min % 60
  return `${hr}h ${String(remMin).padStart(2, '0')}m`
}

function formatTimestamp(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

function StageStatusIcon({ status }: { status: TimelineStageStatus }) {
  if (status === 'completed') {
    return (
      <svg className="h-4 w-4 text-green-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  if (status === 'failed') {
    return (
      <svg className="h-4 w-4 text-red-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
      </svg>
    )
  }
  if (status === 'running') {
    return (
      <span className="relative flex h-4 w-4 shrink-0">
        <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75" />
        <span className="relative inline-flex rounded-full h-4 w-4 bg-blue-500" />
      </span>
    )
  }
  if (status === 'awaiting_approval') {
    return (
      <svg className="h-4 w-4 text-amber-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM7 10a.75.75 0 01.75-.75h4.49L10.28 7.29a.75.75 0 111.04-1.08l3.25 3.09a.75.75 0 010 1.08l-3.25 3.09a.75.75 0 11-1.04-1.08l1.96-1.89H7.75A.75.75 0 017 10z" clipRule="evenodd" />
      </svg>
    )
  }
  return <span className="inline-block h-4 w-4 rounded-full border-2 border-gray-300 shrink-0" />
}

function RoundStatusIcon({ status, verdict }: { status: string; verdict?: 'PASS' | 'FAIL' }) {
  if (status === 'completed' || verdict === 'PASS') {
    return (
      <svg className="h-3 w-3 text-green-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
      </svg>
    )
  }
  if (status === 'failed' || verdict === 'FAIL') {
    return (
      <svg className="h-3 w-3 text-red-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
      </svg>
    )
  }
  if (status === 'running') {
    return (
      <span className="relative flex h-3 w-3 shrink-0">
        <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75" />
        <span className="relative inline-flex rounded-full h-3 w-3 bg-blue-500" />
      </span>
    )
  }
  return <span className="inline-block h-3 w-3 rounded-full border-2 border-gray-300 shrink-0" />
}

function TaskStatusIcon({ status }: { status: string }) {
  switch (status) {
    case 'passed':
      return (
        <svg className="h-3 w-3 text-green-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
        </svg>
      )
    case 'running':
      return (
        <span className="relative flex h-3 w-3 shrink-0">
          <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75" />
          <span className="relative inline-flex rounded-full h-3 w-3 bg-blue-500" />
        </span>
      )
    case 'failed':
      return (
        <svg className="h-3 w-3 text-red-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
        </svg>
      )
    case 'retrying':
      return (
        <svg className="h-3 w-3 text-orange-500 shrink-0" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M15.312 11.424a5.5 5.5 0 01-9.201 2.466l-.312-.311h2.433a.75.75 0 000-1.5H4.598a.75.75 0 00-.75.75v3.634a.75.75 0 001.5 0v-2.233l.312.311a7 7 0 0011.712-3.138.75.75 0 00-1.449-.39zm-10.624-2.85a5.5 5.5 0 019.2-2.464l.311.311h-2.432a.75.75 0 000 1.5h3.634a.75.75 0 00.75-.75V3.538a.75.75 0 00-1.5 0v2.234l-.311-.312a7 7 0 00-11.712 3.138.75.75 0 001.449.39z" clipRule="evenodd" />
        </svg>
      )
    default:
      return <span className="inline-block h-3 w-3 rounded-full border-2 border-gray-300 shrink-0" />
  }
}

function isExpandable(node: TimelineNode): boolean {
  if (node.stage === 'created' || node.stage === 'approved') return false
  const stageNode = node as TimelineStageNode
  return (
    stageNode.status === 'completed' ||
    stageNode.status === 'failed' ||
    stageNode.status === 'running' ||
    stageNode.status === 'awaiting_approval'
  )
}

function hasExpandedContent(node: TimelineStageNode): boolean {
  return (node.stage === 'plan' && node.rounds.length > 0) ||
    (node.stage === 'build' && node.tasks.length > 0)
}

function PlanRounds({ rounds }: { rounds: TimelineRound[] }) {
  return (
    <div className="space-y-1.5 pl-1">
      {rounds.map((round) => (
        <div key={`${round.roundIndex}-${round.label}`} className="flex items-center gap-2">
          <RoundStatusIcon
            status={round.completedAt ? 'completed' : round.startedAt ? 'running' : 'pending'}
            verdict={round.verdict}
          />
          <span className="text-xs text-gray-700">{round.label}</span>
          {round.duration != null && (
            <span className="text-xs text-gray-400 ml-auto">{formatDuration(round.duration)}</span>
          )}
          {round.verdict === 'FAIL' && (
            <span className="text-xs text-red-500 font-medium">FAIL</span>
          )}
        </div>
      ))}
    </div>
  )
}

function BuildTasks({ tasks }: { tasks: TimelineTask[] }) {
  return (
    <div className="space-y-1.5 pl-1">
      {tasks.map((task) => (
        <div key={task.taskId} className="flex items-center gap-2">
          <TaskStatusIcon status={task.status} />
          <span className="text-xs font-mono text-gray-700">{task.taskId}</span>
          <span className="text-xs text-gray-500 truncate flex-1">{task.title}</span>
          {task.error && (
            <span className="text-xs text-red-500 truncate max-w-20 sm:max-w-30" title={task.error}>
              {task.error}
            </span>
          )}
        </div>
      ))}
    </div>
  )
}

function StageExpandedContent({
  node,
  issueNumber,
}: {
  node: TimelineStageNode
  issueNumber: number
}) {
  return (
    <div className="ml-4 sm:ml-6 mt-1 mb-1 pl-2 sm:pl-3 border-l-2 border-gray-200 space-y-2">
      {node.stage === 'plan' && node.rounds.length > 0 && (
        <PlanRounds rounds={node.rounds} />
      )}
      {node.stage === 'build' && node.tasks.length > 0 && (
        <BuildTasks tasks={node.tasks} />
      )}
      {node.model && (
        <div className="text-xs text-gray-400 pt-0.5 truncate" title={node.model}>
          Model: {node.model}
        </div>
      )}
      {node.sessionId && (
        <Link
          to={`/issue/${issueNumber}/session/${node.sessionId}`}
          className="inline-flex items-center gap-1 text-xs text-blue-600 hover:text-blue-800 transition-colors"
        >
          View session
          <svg className="h-3 w-3" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M3 10a.75.75 0 01.75-.75h10.638L10.23 5.29a.75.75 0 111.04-1.08l5.5 5.25a.75.75 0 010 1.08l-5.5 5.25a.75.75 0 11-1.04-1.08l4.158-3.96H3.75A.75.75 0 013 10z" clipRule="evenodd" />
          </svg>
        </Link>
      )}
    </div>
  )
}

function TimelineStageRow({
  node,
  isLast,
  expanded,
  onToggle,
  issueNumber,
}: {
  node: TimelineNode
  isLast: boolean
  expanded: boolean
  onToggle: () => void
  issueNumber: number
}) {
  const expandable = isExpandable(node)

  if (node.stage === 'created') {
    const created = node as TimelineCreatedNode
    return (
      <div className="flex gap-3">
        <div className="flex flex-col items-center shrink-0">
          <span className="inline-block h-3 w-3 rounded-full bg-gray-400 mt-0.5" />
          {!isLast && <div className="w-px flex-1 bg-gray-200 mt-1" />}
        </div>
        <div className="pb-4 min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="text-sm font-medium text-gray-700">Created</span>
            <span className="text-xs text-gray-400">{formatTimestamp(created.timestamp)}</span>
          </div>
        </div>
      </div>
    )
  }

  if (node.stage === 'approved') {
    const approved = node as TimelineApprovedNode
    return (
      <div className="flex gap-3">
        <div className="flex flex-col items-center shrink-0">
          <svg className="h-3.5 w-3.5 text-green-500 mt-0.5 shrink-0" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
          </svg>
          {!isLast && <div className="w-px flex-1 bg-gray-200 mt-1" />}
        </div>
        <div className="pb-4 min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="text-sm font-medium text-green-700">Approved</span>
            <span className="text-xs text-gray-400">{formatTimestamp(approved.timestamp)}</span>
          </div>
        </div>
      </div>
    )
  }

  const stageNode = node as TimelineStageNode
  const isPending = stageNode.status === 'pending'
  const isRunning = stageNode.status === 'running'
  const isAwaiting = stageNode.status === 'awaiting_approval'
  const showExpanded = expanded && hasExpandedContent(stageNode)

  return (
    <div className="flex gap-3">
      <div className="flex flex-col items-center shrink-0">
        <div className="mt-0.5">
          <StageStatusIcon status={stageNode.status} />
        </div>
        {!isLast && (
          <div className={`w-px flex-1 mt-1 ${isPending ? 'bg-gray-200' : 'bg-gray-300'}`} />
        )}
      </div>
      <div className={`min-w-0 flex-1 ${isLast ? '' : 'pb-4'}`}>
        <button
          onClick={expandable ? onToggle : undefined}
          className={`flex items-center gap-2 w-full text-left ${
            expandable ? 'cursor-pointer hover:bg-gray-50 rounded px-0.5 -mx-0.5' : 'cursor-default'
          }`}
        >
          <span className={`text-sm font-medium ${
            isPending ? 'text-gray-400' :
            isRunning ? 'text-blue-700' :
            isAwaiting ? 'text-amber-700' :
            stageNode.status === 'failed' ? 'text-red-700' :
            'text-gray-700'
          }`}>
            {stageNode.label}
          </span>
          {stageNode.status === 'awaiting_approval' && (
            <span className="text-xs text-amber-500 font-medium">Awaiting approval</span>
          )}
          {stageNode.status === 'running' && (
            <span className="text-xs text-blue-500 font-medium">Running</span>
          )}
          {stageNode.durationMs != null && stageNode.status !== 'pending' && (
            <span className="text-xs text-gray-400">{formatDuration(stageNode.durationMs)}</span>
          )}
          {expandable && hasExpandedContent(stageNode) && (
            <svg
              className={`h-3 w-3 text-gray-400 shrink-0 transition-transform ml-auto ${expanded ? 'rotate-90' : ''}`}
              viewBox="0 0 20 20"
              fill="currentColor"
            >
              <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
            </svg>
          )}
        </button>
        {showExpanded && (
          <StageExpandedContent node={stageNode} issueNumber={issueNumber} />
        )}
      </div>
    </div>
  )
}

export function IssueTimeline({ issueNumber }: { issueNumber: number }) {
  const { timeline, isLoading } = useIssueTimeline(issueNumber)
  const [expandedStages, setExpandedStages] = useState<Set<string>>(new Set())

  if (isLoading) {
    return (
      <div className="mb-6 py-3">
        <div className="text-sm text-gray-400">Loading timeline...</div>
      </div>
    )
  }

  if (timeline.length === 0) return null

  const toggleStage = (stage: string) => {
    setExpandedStages((prev) => {
      const next = new Set(prev)
      if (next.has(stage)) {
        next.delete(stage)
      } else {
        next.add(stage)
      }
      return next
    })
  }

  return (
    <div className="mb-6 overflow-hidden">
      {timeline.map((node, i) => (
        <TimelineStageRow
          key={`${node.stage}-${i}`}
          node={node}
          isLast={i === timeline.length - 1}
          expanded={expandedStages.has(node.stage)}
          onToggle={() => toggleStage(node.stage)}
          issueNumber={issueNumber}
        />
      ))}
    </div>
  )
}
