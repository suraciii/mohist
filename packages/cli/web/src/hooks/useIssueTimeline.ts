import { useEffect, useRef, useState, useCallback } from 'react'
import { useIssue, useBuildStatus, useTasks } from './useQueries'
import { useCoderSessions } from './useCoderSessions'
import { onAgentEvent } from '../lib/agent-events'
import { api } from '../lib/api'
import { reconstructRoundsFromLogs } from './useSessionTimeline'
import type {
  Stage,
  CoderSessionItem,
  WorkflowLogItem,
  AgentDetailEventMap,
  TaskProgressEntry,
} from '../lib/types'

const STAGE_ORDER: Stage[] = ['plan', 'build', 'check', 'done'] as Stage[]

const TIMELINE_LABEL_MAP: Record<string, string> = {
  plan: 'Plan',
  build: 'Build',
  check: 'Review',
  done: 'Done',
}

export type TimelineStageStatus = 'pending' | 'running' | 'completed' | 'failed' | 'awaiting_approval'

interface PlanStep {
  roundType: string
  roundLabel: string
  roundIndex: number
  status: 'pending' | 'running' | 'completed' | 'failed'
  duration?: number
  verdict?: 'PASS' | 'FAIL'
}

export interface TimelineRound {
  roundIndex: number
  label: string
  startedAt: string
  completedAt: string | null
  duration?: number
  verdict?: 'PASS' | 'FAIL'
}

export interface TimelineTask {
  taskId: string
  title: string
  status: 'pending' | 'running' | 'passed' | 'failed' | 'retrying'
  duration?: number
  error?: string
}

export interface TimelineStageNode {
  stage: string
  label: string
  status: TimelineStageStatus
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
  sessionId: string | null
  model: string | null
  rounds: TimelineRound[]
  tasks: TimelineTask[]
}

export interface TimelineCreatedNode {
  stage: 'created'
  label: 'Created'
  timestamp: string
}

export interface TimelineApprovedNode {
  stage: 'approved'
  label: 'Approved'
  timestamp: string
  status: 'completed'
}

export type TimelineNode = TimelineCreatedNode | TimelineStageNode | TimelineApprovedNode

function getSessionForStage(sessions: CoderSessionItem[], stage: string): CoderSessionItem | undefined {
  return sessions.find((s) => s.stage === stage)
}

function computeDurationMs(start: string | null, end: string | null): number | null {
  if (!start) return null
  const endTime = end ? new Date(end).getTime() : Date.now()
  return endTime - new Date(start).getTime()
}

function inferStageStatus(
  stage: string,
  issueStage: string,
  session: CoderSessionItem | undefined,
  approvalState?: { status: string; stage?: string } | null,
): TimelineStageStatus {
  const stageIndex = STAGE_ORDER.indexOf(stage as Stage)
  const currentIndex = STAGE_ORDER.indexOf(issueStage as Stage)

  if (stageIndex > currentIndex) return 'pending'
  if (stageIndex < currentIndex) {
    if (session?.status === 'failed') return 'failed'
    return 'completed'
  }

  if (stage === 'plan' && approvalState?.status === 'awaiting' && approvalState.stage === 'plan') {
    return 'awaiting_approval'
  }
  if (stage === 'check' && approvalState?.status === 'awaiting' && approvalState.stage === 'check') {
    return 'awaiting_approval'
  }

  if (session?.status === 'running') return 'running'
  if (session?.status === 'failed') return 'failed'
  if (session?.status === 'completed') return 'completed'

  return 'running'
}

function buildRoundsFromSession(session: CoderSessionItem): TimelineRound[] {
  const rounds = reconstructRoundsFromLogs(session.workflowLogs)
  return rounds.map((r) => ({
    roundIndex: r.roundIndex,
    label: r.label,
    startedAt: r.startedAt,
    completedAt: r.completedAt,
    duration: r.completedAt && r.startedAt
      ? new Date(r.completedAt).getTime() - new Date(r.startedAt).getTime()
      : undefined,
  }))
}

function buildTimeline(
  issueData: { createdAt: string; stage: string; approvalState?: { status: string; requestedAt: string; approvedAt?: string; stage?: string } | null } | null | undefined,
  sessions: CoderSessionItem[],
  _logs: WorkflowLogItem[],
  taskProgress: Map<string, TaskProgressEntry>,
  planSteps: PlanStep[],
): TimelineNode[] {
  const nodes: TimelineNode[] = []

  if (!issueData) return nodes

  nodes.push({
    stage: 'created',
    label: 'Created',
    timestamp: issueData.createdAt,
  })

  const currentStage = issueData.stage as string

  for (const stage of STAGE_ORDER) {
    if (stage === 'done' && currentStage !== 'done') {
      nodes.push({
        stage: 'done',
        label: 'Done',
        status: 'pending',
        startedAt: null,
        completedAt: null,
        durationMs: null,
        sessionId: null,
        model: null,
        rounds: [],
        tasks: [],
      })
      continue
    }

    const session = getSessionForStage(sessions, stage)
    const status = inferStageStatus(stage, currentStage, session, issueData.approvalState)

    let rounds: TimelineRound[] = []
    if (stage === 'plan' && session) {
      if (planSteps.length > 0) {
        rounds = planSteps.map((s, i) => ({
          roundIndex: i,
          label: s.roundLabel,
          startedAt: '',
          completedAt: s.status === 'completed' || s.status === 'failed' ? new Date().toISOString() : null,
          duration: s.duration,
          verdict: s.verdict,
        }))
      } else {
        rounds = buildRoundsFromSession(session)
      }
    }

    let tasks: TimelineTask[] = []
    if (stage === 'build') {
      tasks = Array.from(taskProgress.values()).map((t) => ({
        taskId: t.taskId,
        title: `Task ${t.taskIndex + 1}`,
        status: t.status,
        error: t.error,
      }))
    }

    nodes.push({
      stage,
      label: TIMELINE_LABEL_MAP[stage] ?? stage,
      status,
      startedAt: session?.createdAt ?? null,
      completedAt: session?.completedAt ?? null,
      durationMs: computeDurationMs(session?.createdAt ?? null, session?.completedAt ?? null),
      sessionId: session?.id ?? null,
      model: session?.model ?? null,
      rounds,
      tasks,
    })

    if (
      stage === 'plan' &&
      issueData.approvalState?.status === 'approved' &&
      issueData.approvalState.approvedAt
    ) {
      nodes.push({
        stage: 'approved',
        label: 'Approved',
        timestamp: issueData.approvalState.approvedAt,
        status: 'completed',
      })
    }
  }

  return nodes
}

const FLUSH_INTERVAL = 100

type SSEEventPayload =
  | AgentDetailEventMap['plan_round_start']
  | AgentDetailEventMap['plan_round_complete']
  | AgentDetailEventMap['ralph_task_update']
  | AgentDetailEventMap['coder_session_started']
  | AgentDetailEventMap['coder_session_completed']

interface PendingSSEEvent {
  type: string
  payload: SSEEventPayload
}

export function useIssueTimeline(issueNumber: number) {
  const { data: issue, isLoading: issueLoading } = useIssue(issueNumber)
  const { sessions, isLoading: sessionsLoading } = useCoderSessions(issueNumber)
  const { data: buildStatus } = useBuildStatus(issueNumber)
  const { data: tasksData } = useTasks(issueNumber)

  const [taskProgress, setTaskProgress] = useState<Map<string, TaskProgressEntry>>(new Map())
  const [planSteps, setPlanSteps] = useState<PlanStep[]>([])
  const [timeline, setTimeline] = useState<TimelineNode[]>([])

  const logsRef = useRef<WorkflowLogItem[]>([])
  const pendingRef = useRef<PendingSSEEvent[]>([])
  const rafRef = useRef<number | null>(null)
  const timeoutRef = useRef<number | null>(null)
  const lastFlushRef = useRef(0)
  const mountedRef = useRef(true)
  const logsFetchedRef = useRef(false)

  useEffect(() => {
    if (issueNumber <= 0) return
    let cancelled = false
    logsFetchedRef.current = false
    api.getWorkflowLogs(issueNumber).then((logs) => {
      if (cancelled) return
      logsRef.current = logs
      logsFetchedRef.current = true
    })
    return () => { cancelled = true }
  }, [issueNumber])

  useEffect(() => {
    if (buildStatus?.tasks) {
      setTaskProgress((prev) => {
        const next = new Map(prev)
        for (const task of buildStatus.tasks) {
          const existing = next.get(task.id)
          const status: TaskProgressEntry['status'] = task.passes ? 'passed' : (task.error ? 'failed' : (existing?.status ?? 'pending'))
          next.set(task.id, {
            taskId: task.id,
            taskIndex: existing?.taskIndex ?? next.size,
            totalTasks: buildStatus.tasks.length,
            status,
            error: task.error ?? existing?.error,
          })
        }
        return next
      })
    }
  }, [buildStatus])

  useEffect(() => {
    if (!tasksData?.tasks) return
    setTaskProgress((prev) => {
      const next = new Map(prev)
      tasksData.tasks.forEach((task, i) => {
        const existing = next.get(task.id)
        const status: TaskProgressEntry['status'] = task.passes ? 'passed' : (task.error ? 'failed' : (existing?.status ?? 'pending'))
        next.set(task.id, {
          taskId: task.id,
          taskIndex: i,
          totalTasks: tasksData.tasks.length,
          status,
          error: task.error ?? existing?.error,
        })
      })
      return next
    })
  }, [tasksData])

  const flushPending = useCallback(() => {
    if (!mountedRef.current) return
    const batch = pendingRef.current
    pendingRef.current = []
    if (batch.length === 0) {
      rafRef.current = null
      return
    }

    for (const event of batch) {
      if (event.type === 'ralph_task_update') {
        const detail = event.payload as AgentDetailEventMap['ralph_task_update']
        const statusMap: Record<string, TaskProgressEntry['status']> = {
          started: 'running',
          completed: 'passed',
          failed: 'failed',
          retrying: 'retrying',
        }
        setTaskProgress((prev) => {
          const next = new Map(prev)
          const existing = next.get(detail.taskId)
          next.set(detail.taskId, {
            taskId: detail.taskId,
            taskIndex: detail.taskIndex,
            totalTasks: detail.totalTasks,
            status: statusMap[detail.status] ?? 'pending',
            executionId: detail.executionId,
            attempt: detail.attempt ?? existing?.attempt,
            error: detail.error ?? existing?.error,
          })
          return next
        })
      } else if (event.type === 'plan_round_start') {
        const detail = event.payload as AgentDetailEventMap['plan_round_start']
        setPlanSteps((prev) => {
          const steps: PlanStep[] = prev.length > 0 ? [...prev] : BASE_PLAN_STEPS.map((s, i) => ({
            roundType: s.roundType,
            roundLabel: s.roundLabel,
            roundIndex: i,
            status: 'pending' as const,
          }))
          const idx = steps.findIndex((s) => s.roundType === detail.roundType)
          if (idx >= 0) {
            steps[idx] = { ...steps[idx], status: 'running' as PlanStep['status'] }
          } else {
            steps.push({
              roundType: detail.roundType,
              roundLabel: detail.roundLabel ?? detail.roundType,
              roundIndex: detail.roundIndex,
              status: 'running' as PlanStep['status'],
            })
          }
          return steps
        })
      } else if (event.type === 'plan_round_complete') {
        const detail = event.payload as AgentDetailEventMap['plan_round_complete']
        setPlanSteps((prev) => {
          const steps = prev.length > 0 ? [...prev] : BASE_PLAN_STEPS.map((s, i) => ({
            roundType: s.roundType,
            roundLabel: s.roundLabel,
            roundIndex: i,
            status: i < detail.roundIndex ? ('completed' as const) : ('pending' as const),
          }))
          const isFailed = detail.verdict === 'FAIL'
          const idx = steps.findIndex((s) => s.roundType === detail.roundType)
          if (idx >= 0) {
            steps[idx] = {
              ...steps[idx],
              status: isFailed ? ('failed' as const) : ('completed' as const),
              duration: detail.duration,
              ...(detail.verdict ? { verdict: detail.verdict as 'PASS' | 'FAIL' } : {}),
            }
          }
          return steps
        })
      }
    }

    lastFlushRef.current = Date.now()
    rafRef.current = null
  }, [])

  const scheduleFlush = useCallback(() => {
    if (!mountedRef.current) return
    if (rafRef.current !== null || timeoutRef.current !== null) return
    const now = Date.now()
    const elapsed = now - lastFlushRef.current
    if (elapsed >= FLUSH_INTERVAL) {
      rafRef.current = requestAnimationFrame(flushPending)
    } else {
      timeoutRef.current = window.setTimeout(() => {
        timeoutRef.current = null
        if (mountedRef.current) {
          rafRef.current = requestAnimationFrame(flushPending)
        }
      }, FLUSH_INTERVAL - elapsed)
    }
  }, [flushPending])

  useEffect(() => {
    mountedRef.current = true
    const issueId = String(issueNumber)
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('plan_round_start', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        pendingRef.current.push({ type: 'plan_round_start', payload: detail })
        scheduleFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('plan_round_complete', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        pendingRef.current.push({ type: 'plan_round_complete', payload: detail })
        scheduleFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('ralph_task_update', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
        pendingRef.current.push({ type: 'ralph_task_update', payload: detail })
        scheduleFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_started', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail) => {
        if (detail.issueId !== issueId || !mountedRef.current) return
      }),
    )

    return () => {
      mountedRef.current = false
      for (const unsub of unsubs) unsub()
      if (rafRef.current !== null) {
        cancelAnimationFrame(rafRef.current)
        rafRef.current = null
      }
      if (timeoutRef.current !== null) {
        clearTimeout(timeoutRef.current)
        timeoutRef.current = null
      }
    }
  }, [issueNumber, scheduleFlush])

  useEffect(() => {
    if (issueLoading || sessionsLoading) return
    const result = buildTimeline(issue, sessions, logsRef.current, taskProgress, planSteps)
    setTimeline(result)
  }, [issue, sessions, issueLoading, sessionsLoading, taskProgress, planSteps])

  return {
    timeline,
    isLoading: issueLoading || sessionsLoading,
  }
}

const BASE_PLAN_STEPS: Array<{ roundType: string; roundLabel: string }> = [
  { roundType: 'proposal', roundLabel: 'Proposal' },
  { roundType: 'specs', roundLabel: 'Specs' },
  { roundType: 'design', roundLabel: 'Design' },
  { roundType: 'tasks', roundLabel: 'Tasks' },
  { roundType: 'self-review', roundLabel: 'Self Review' },
]
