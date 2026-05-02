import { useEffect, useRef, useState, useCallback } from 'react'
import { useAgentSessions, useAgentStatus } from './useQueries'
import { onAgentEvent } from '../lib/agent-events'
import type { AgentSessionInfo } from '../lib/types'

const RAF_BATCH_INTERVAL = 100
const MAX_PREVIEW_LENGTH = 80
const MAX_ACTIVITY_PREVIEWS = 3

export interface TaskProgress {
  completed: number
  total: number
}

export interface ActivityPreview {
  kind: 'text' | 'tool'
  text: string
}

export interface WaitingCard {
  issueId: string
  issueNumber: string
  label: 'Needs Approval' | 'Question Pending'
  questionPreview?: string
  questionId?: string
  questionAskedAt?: string
}

export interface SessionCard {
  issueNumber: string
  issueTitle: string
  issueStage: string
  sessionId: string
  status: string
  model: string | null
  taskDescription: string | null
  title: string | null
  createdAt: string
  completedAt: string | null
  lastActivityAt: string | null
  activityPreviews: ActivityPreview[]
  taskProgress: TaskProgress | null
}

export type SessionCardMap = Map<string, SessionCard>

export interface StatusCounts {
  active: number
  waiting: number
  completed: number
  failed: number
}

interface TextChunkBatch {
  issueId: string
  text: string
}

function truncate(text: string, max: number): string {
  if (text.length <= max) return text
  return text.slice(0, max - 1) + '\u2026'
}

function sessionToCard(s: AgentSessionInfo): SessionCard {
  return {
    issueNumber: String(s.issueNumber),
    issueTitle: s.issueTitle,
    issueStage: s.issueStage,
    sessionId: s.sessionId,
    status: s.status,
    model: s.model,
    taskDescription: s.taskDescription,
    title: null,
    createdAt: s.createdAt,
    completedAt: s.completedAt,
    lastActivityAt: s.lastActivityAt,
    activityPreviews: [],
    taskProgress: null,
  }
}

export function useActivityCards() {
  const { data: sessionsData } = useAgentSessions()
  const { data: agentStatus } = useAgentStatus()

  const activeRef = useRef<SessionCardMap>(new Map())
  const recentRef = useRef<SessionCardMap>(new Map())
  const waitingRef = useRef<Map<string, WaitingCard>>(new Map())
  const [, setTick] = useState(0)
  const mountedRef = useRef(true)

  const textChunkBufferRef = useRef<TextChunkBatch[]>([])
  const rafRef = useRef<number | null>(null)
  const timeoutRef = useRef<number | null>(null)
  const lastFlushRef = useRef(0)

  const forceUpdate = useCallback(() => {
    if (mountedRef.current) {
      setTick((t) => t + 1)
    }
  }, [])

  const flushTextChunks = useCallback(() => {
    if (!mountedRef.current) return
    const batch = textChunkBufferRef.current
    textChunkBufferRef.current = []
    rafRef.current = null

    if (batch.length === 0) return

    const perIssue = new Map<string, string>()
    for (const chunk of batch) {
      const existing = perIssue.get(chunk.issueId) ?? ''
      perIssue.set(chunk.issueId, existing + chunk.text)
    }

    let changed = false
    for (const [issueId, mergedText] of perIssue) {
      const card = activeRef.current.get(issueId)
      if (!card) continue
      const preview: ActivityPreview = {
        kind: 'text',
        text: truncate(mergedText, MAX_PREVIEW_LENGTH),
      }
      const previews = [preview, ...card.activityPreviews]
      card.activityPreviews = previews.slice(0, MAX_ACTIVITY_PREVIEWS)
      card.lastActivityAt = new Date().toISOString()
      changed = true
    }

    if (changed) forceUpdate()
    lastFlushRef.current = Date.now()
  }, [forceUpdate])

  const scheduleTextChunkFlush = useCallback(() => {
    if (!mountedRef.current) return
    if (rafRef.current !== null || timeoutRef.current !== null) return
    const now = Date.now()
    const elapsed = now - lastFlushRef.current
    if (elapsed >= RAF_BATCH_INTERVAL) {
      rafRef.current = requestAnimationFrame(flushTextChunks)
    } else {
      timeoutRef.current = window.setTimeout(() => {
        timeoutRef.current = null
        if (mountedRef.current) {
          rafRef.current = requestAnimationFrame(flushTextChunks)
        }
      }, RAF_BATCH_INTERVAL - elapsed)
    }
  }, [flushTextChunks])

  useEffect(() => {
    if (!sessionsData) return
    const newActive = new Map<string, SessionCard>()
    const newRecent = new Map<string, SessionCard>()

    for (const s of sessionsData) {
      const key = String(s.issueNumber)
      const card = sessionToCard(s)

      if (s.status === 'running') {
        const existing = activeRef.current.get(key)
        if (existing) {
          card.activityPreviews = existing.activityPreviews
          card.taskProgress = existing.taskProgress
        }
        newActive.set(key, card)
      } else {
        const existingRecent = recentRef.current.get(key)
        if (existingRecent) {
          card.activityPreviews = existingRecent.activityPreviews
          card.taskProgress = existingRecent.taskProgress
        }
        newRecent.set(key, card)
      }
    }

    for (const [key, card] of recentRef.current) {
      if (!newRecent.has(key)) {
        newRecent.set(key, card)
      }
    }

    activeRef.current = newActive
    recentRef.current = newRecent
    forceUpdate()
  }, [sessionsData, forceUpdate])

  useEffect(() => {
    if (!agentStatus) return
    const waitingMap = new Map<string, WaitingCard>()

    for (const q of agentStatus.waitingQuestions ?? []) {
      const key = String(q.issueNumber)
      const existing = waitingRef.current.get(key)
      waitingMap.set(key, {
        issueId: q.issueId,
        issueNumber: key,
        label: 'Question Pending',
        questionPreview: truncate(q.question, MAX_PREVIEW_LENGTH),
        questionId: q.questionId,
        questionAskedAt: existing?.questionAskedAt ?? new Date().toISOString(),
      })
    }

    for (const r of agentStatus.recoverableIssues ?? []) {
      const key = String(r.issueNumber)
      if (!waitingMap.has(key)) {
        waitingMap.set(key, {
          issueId: key,
          issueNumber: key,
          label: 'Needs Approval',
        })
      }
    }

    for (const agent of agentStatus.activeAgents ?? []) {
      const existingKey = String(agent.issueNumber)
      const existing = waitingRef.current.get(existingKey)
      if (existing && existing.label === 'Needs Approval' && !waitingMap.has(existingKey)) {
        waitingMap.set(existingKey, existing)
      }
    }

    waitingRef.current = waitingMap
    forceUpdate()
  }, [agentStatus, forceUpdate])

  useEffect(() => {
    mountedRef.current = true
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_session_started', (detail) => {
        if (!mountedRef.current) return
        const key = detail.issueId
        const card: SessionCard = {
          issueNumber: key,
          issueTitle: '',
          issueStage: detail.stage ?? '',
          sessionId: detail.coderSessionId,
          status: 'running',
          model: detail.model ?? null,
          taskDescription: detail.taskDescription ?? null,
          title: detail.title ?? null,
          createdAt: new Date().toISOString(),
          completedAt: null,
          lastActivityAt: new Date().toISOString(),
          activityPreviews: [],
          taskProgress: null,
        }
        activeRef.current.set(key, card)
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail) => {
        if (!mountedRef.current) return
        const key = detail.issueId
        const card = activeRef.current.get(key)
        if (!card) return
        card.status = detail.status === 'completed' ? 'completed' : 'failed'
        card.completedAt = new Date().toISOString()
        activeRef.current.delete(key)
        recentRef.current.set(key, card)
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail) => {
        if (!mountedRef.current) return
        if (!activeRef.current.has(detail.issueId)) return
        textChunkBufferRef.current.push({
          issueId: detail.issueId,
          text: detail.text,
        })
        scheduleTextChunkFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail) => {
        if (!mountedRef.current) return
        const card = activeRef.current.get(detail.issueId)
        if (!card) return
        const title = detail.title ?? detail.toolName
        const preview: ActivityPreview = {
          kind: 'tool',
          text: truncate(title, MAX_PREVIEW_LENGTH),
        }
        const previews = [preview, ...card.activityPreviews]
        card.activityPreviews = previews.slice(0, MAX_ACTIVITY_PREVIEWS)
        card.lastActivityAt = new Date().toISOString()
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('ralph_task_update', (detail) => {
        if (!mountedRef.current) return
        const card = activeRef.current.get(detail.issueId)
        if (!card) return
        const prev = card.taskProgress?.completed ?? 0
        const completed = detail.status === 'completed' ? prev + 1 : prev
        card.taskProgress = {
          completed,
          total: detail.totalTasks,
        }
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('ralph_loop_progress', (detail) => {
        if (!mountedRef.current) return
        const card = activeRef.current.get(detail.issueId)
        if (!card) return
        card.taskProgress = {
          completed: detail.completed,
          total: detail.total,
        }
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('agent_paused', (detail) => {
        if (!mountedRef.current) return
        const key = detail.issueId
        waitingRef.current.set(key, {
          issueId: detail.issueId,
          issueNumber: key,
          label: 'Needs Approval',
        })
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('question_asked', (detail) => {
        if (!mountedRef.current) return
        const key = detail.issueId
        waitingRef.current.set(key, {
          issueId: detail.issueId,
          issueNumber: key,
          label: 'Question Pending',
          questionPreview: truncate(detail.question, MAX_PREVIEW_LENGTH),
          questionId: detail.questionId,
          questionAskedAt: new Date().toISOString(),
        })
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('question_answered', (detail) => {
        if (!mountedRef.current) return
        const key = detail.issueId
        const existing = waitingRef.current.get(key)
        if (existing && existing.label === 'Question Pending') {
          waitingRef.current.delete(key)
          forceUpdate()
        }
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
  }, [forceUpdate, scheduleTextChunkFlush])

  const activeCards = Array.from(activeRef.current.values())
    .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())

  const recentCards = Array.from(recentRef.current.values())
    .sort((a, b) => {
      const aTime = a.completedAt ? new Date(a.completedAt).getTime() : 0
      const bTime = b.completedAt ? new Date(b.completedAt).getTime() : 0
      return bTime - aTime
    })

  const waitingCards = Array.from(waitingRef.current.values())

  const completedCount = recentCards.filter((c) => c.status === 'completed').length
  const failedCount = recentCards.filter((c) => c.status === 'failed').length

  const statusCounts: StatusCounts = {
    active: activeCards.length,
    waiting: waitingCards.length,
    completed: completedCount,
    failed: failedCount,
  }

  const slotUsage = {
    active: agentStatus?.activeAgents?.length ?? 0,
    max: agentStatus?.maxConcurrentAgents ?? 0,
  }

  return {
    activeCards,
    recentCards,
    waitingCards,
    statusCounts,
    slotUsage,
  }
}
