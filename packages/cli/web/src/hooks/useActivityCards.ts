import { useEffect, useRef, useState, useCallback } from 'react'
import { useAgentSessions, useAgentStatus } from './useQueries'
import { onAgentEvent } from '../lib/agent-events'
import type { AgentDetailEventMap, AgentSessionInfo } from '../lib/types'

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

  const cardsRef = useRef<SessionCardMap>(new Map())
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
      const card = cardsRef.current.get(issueId)
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
    const map = new Map<string, SessionCard>()
    for (const s of sessionsData) {
      const key = String(s.issueNumber)
      const existing = cardsRef.current.get(key)
      const card = sessionToCard(s)
      if (existing) {
        card.activityPreviews = existing.activityPreviews
        card.taskProgress = existing.taskProgress
      }
      map.set(key, card)
    }
    cardsRef.current = map
    forceUpdate()
  }, [sessionsData, forceUpdate])

  useEffect(() => {
    if (!agentStatus) return
    const waitingMap = new Map<string, WaitingCard>()

    for (const agent of agentStatus.activeAgents ?? []) {
      const pausedKey = String(agent.issueNumber)
      const existingPaused = waitingRef.current.get(pausedKey)
      if (existingPaused && existingPaused.label === 'Needs Approval') {
        waitingMap.set(pausedKey, existingPaused)
      }
    }

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

    waitingRef.current = waitingMap
    forceUpdate()
  }, [agentStatus, forceUpdate])

  useEffect(() => {
    mountedRef.current = true
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('coder_session_started', (detail: AgentDetailEventMap['coder_session_started']) => {
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
          createdAt: new Date().toISOString(),
          completedAt: null,
          lastActivityAt: new Date().toISOString(),
          activityPreviews: [],
          taskProgress: null,
        }
        cardsRef.current.set(key, card)
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_session_completed', (detail: AgentDetailEventMap['coder_session_completed']) => {
        if (!mountedRef.current) return
        const key = detail.issueId
        const card = cardsRef.current.get(key)
        if (!card) return
        card.status = detail.status === 'completed' ? 'completed' : 'failed'
        card.completedAt = new Date().toISOString()
        cardsRef.current.delete(key)
        const completedKey = `__recent__${key}`
        cardsRef.current.set(completedKey, card)
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_text_chunk', (detail: AgentDetailEventMap['coder_text_chunk']) => {
        if (!mountedRef.current) return
        if (!cardsRef.current.has(detail.issueId)) return
        textChunkBufferRef.current.push({
          issueId: detail.issueId,
          text: detail.text,
        })
        scheduleTextChunkFlush()
      }),
    )

    unsubs.push(
      onAgentEvent('coder_tool_call', (detail: AgentDetailEventMap['coder_tool_call']) => {
        if (!mountedRef.current) return
        const card = cardsRef.current.get(detail.issueId)
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
      onAgentEvent('ralph_task_update', (detail: AgentDetailEventMap['ralph_task_update']) => {
        if (!mountedRef.current) return
        const card = cardsRef.current.get(detail.issueId)
        if (!card) return
        const completed = detail.status === 'completed' ? (card.taskProgress?.completed ?? 0) + 1 : (card.taskProgress?.completed ?? 0)
        card.taskProgress = {
          completed,
          total: detail.totalTasks,
        }
        forceUpdate()
      }),
    )

    unsubs.push(
      onAgentEvent('ralph_loop_progress', (detail: AgentDetailEventMap['ralph_loop_progress']) => {
        if (!mountedRef.current) return
        const card = cardsRef.current.get(detail.issueId)
        if (!card) return
        card.taskProgress = {
          completed: detail.completed,
          total: detail.total,
        }
        forceUpdate()
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

  const activeCards: SessionCard[] = []
  const recentCards: SessionCard[] = []
  const waitingCards: WaitingCard[] = Array.from(waitingRef.current.values())

  for (const card of cardsRef.current.values()) {
    if (card.status === 'running') {
      activeCards.push(card)
    } else if (card.status === 'completed' || card.status === 'failed') {
      recentCards.push(card)
    }
  }

  activeCards.sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())
  recentCards.sort((a, b) => {
    const aTime = a.completedAt ? new Date(a.completedAt).getTime() : 0
    const bTime = b.completedAt ? new Date(b.completedAt).getTime() : 0
    return bTime - aTime
  })

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
