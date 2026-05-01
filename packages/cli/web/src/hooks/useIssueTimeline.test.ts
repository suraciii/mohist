import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import type { CoderSessionItem, TaskProgressEntry } from '../lib/types'
import {
  buildTimeline,
  computeDurationMs,
  inferStageStatus,
  type TimelineStageNode,
} from './useIssueTimeline'

function makeSession(overrides: Partial<CoderSessionItem> = {}): CoderSessionItem {
  return {
    id: 'session-1',
    acpSessionId: 'acp-1',
    executionId: null,
    taskDescription: null,
    status: 'completed',
    createdAt: '2026-04-27T10:00:00.000Z',
    completedAt: '2026-04-27T10:08:26.000Z',
    model: null,
    coderType: null,
    stage: 'plan',
    workflowLogs: [],
    ...overrides,
  }
}

describe('computeDurationMs', () => {
  it('returns null when start is null', () => {
    expect(computeDurationMs(null, '2026-04-27T10:08:26.000Z')).toBeNull()
  })

  it('computes difference between end and start timestamps', () => {
    const start = '2026-04-27T10:00:00.000Z'
    const end = '2026-04-27T10:08:26.000Z'
    const expected = new Date(end).getTime() - new Date(start).getTime()
    expect(computeDurationMs(start, end)).toBe(expected)
  })

  it('computes 8m 26s as 506000ms', () => {
    const ms = computeDurationMs('2026-04-27T03:12:00.000Z', '2026-04-27T03:20:26.000Z')
    expect(ms).toBe(506000)
  })

  it('uses Date.now when end is null', () => {
    vi.useFakeTimers({ now: new Date('2026-04-27T10:05:00.000Z') })
    const ms = computeDurationMs('2026-04-27T10:00:00.000Z', null)
    expect(ms).toBe(300000)
    vi.useRealTimers()
  })
})

describe('inferStageStatus', () => {
  it('returns pending for stages after current', () => {
    expect(inferStageStatus('build', 'plan', undefined, null)).toBe('pending')
  })

  it('returns completed for stages before current with no session failure', () => {
    const session = makeSession({ stage: 'plan', status: 'completed' })
    expect(inferStageStatus('plan', 'build', session, null)).toBe('completed')
  })

  it('returns failed for stages before current with failed session', () => {
    const session = makeSession({ stage: 'plan', status: 'failed' })
    expect(inferStageStatus('plan', 'build', session, null)).toBe('failed')
  })

  it('returns awaiting_approval for plan when approval is awaiting', () => {
    expect(
      inferStageStatus('plan', 'plan', undefined, {
        status: 'awaiting',
        stage: 'plan',
      }),
    ).toBe('awaiting_approval')
  })

  it('returns awaiting_approval for check when approval is awaiting', () => {
    expect(
      inferStageStatus('check', 'check', undefined, {
        status: 'awaiting',
        stage: 'check',
      }),
    ).toBe('awaiting_approval')
  })

  it('returns running when session is running at current stage', () => {
    const session = makeSession({ stage: 'plan', status: 'running' })
    expect(inferStageStatus('plan', 'plan', session, null)).toBe('running')
  })

  it('returns completed when session is completed at current stage', () => {
    const session = makeSession({ stage: 'plan', status: 'completed' })
    expect(inferStageStatus('plan', 'plan', session, null)).toBe('completed')
  })

  it('returns running as fallback for current stage without session', () => {
    expect(inferStageStatus('plan', 'plan', undefined, null)).toBe('running')
  })
})

describe('buildTimeline', () => {
  it('returns empty array when issueData is null', () => {
    expect(buildTimeline(null, [], [], new Map(), [])).toEqual([])
  })

  it('returns empty array when issueData is undefined', () => {
    expect(buildTimeline(undefined, [], [], new Map(), [])).toEqual([])
  })

  it('constructs Created node from issue createdAt', () => {
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'plan' },
      [],
      [],
      new Map(),
      [],
    )
    expect(nodes[0]).toEqual({
      stage: 'created',
      label: 'Created',
      timestamp: '2026-04-27T10:00:00.000Z',
    })
  })

  it('includes all four pipeline stages', () => {
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'plan' },
      [],
      [],
      new Map(),
      [],
    )
    const stageNames = nodes.map((n) => n.stage)
    expect(stageNames).toContain('plan')
    expect(stageNames).toContain('build')
    expect(stageNames).toContain('check')
    expect(stageNames).toContain('done')
  })

  it('marks stages after current as pending', () => {
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'plan' },
      [],
      [],
      new Map(),
      [],
    )
    const buildNode = nodes.find((n) => n.stage === 'build') as TimelineStageNode
    const checkNode = nodes.find((n) => n.stage === 'check') as TimelineStageNode
    const doneNode = nodes.find((n) => n.stage === 'done') as TimelineStageNode
    expect(buildNode.status).toBe('pending')
    expect(checkNode.status).toBe('pending')
    expect(doneNode.status).toBe('pending')
  })

  it('reconstructs completed plan and build stages with durations', () => {
    const planSession = makeSession({
      id: 's-plan',
      stage: 'plan',
      status: 'completed',
      createdAt: '2026-04-27T03:12:00.000Z',
      completedAt: '2026-04-27T03:20:26.000Z',
      model: 'MiniMax-M2.7',
    })
    const buildSession = makeSession({
      id: 's-build',
      stage: 'build',
      status: 'completed',
      createdAt: '2026-04-27T03:21:00.000Z',
      completedAt: '2026-04-27T03:27:10.000Z',
    })

    const nodes = buildTimeline(
      { createdAt: '2026-04-27T03:00:00.000Z', stage: 'check' },
      [planSession, buildSession],
      [],
      new Map(),
      [],
    )

    const planNode = nodes.find((n) => n.stage === 'plan') as TimelineStageNode
    expect(planNode.status).toBe('completed')
    expect(planNode.durationMs).toBe(506000)
    expect(planNode.model).toBe('MiniMax-M2.7')
    expect(planNode.sessionId).toBe('s-plan')

    const buildNode = nodes.find((n) => n.stage === 'build') as TimelineStageNode
    expect(buildNode.status).toBe('completed')
    expect(buildNode.durationMs).toBe(370000)
    expect(buildNode.sessionId).toBe('s-build')
  })

  it('computes build duration as 6m 10s (370000ms)', () => {
    const buildSession = makeSession({
      stage: 'build',
      createdAt: '2026-04-27T03:21:00.000Z',
      completedAt: '2026-04-27T03:27:10.000Z',
    })
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T03:00:00.000Z', stage: 'check' },
      [buildSession],
      [],
      new Map(),
      [],
    )
    const buildNode = nodes.find((n) => n.stage === 'build') as TimelineStageNode
    expect(buildNode.durationMs).toBe(370000)
    expect(buildNode.durationMs! / 1000).toBe(370)
  })

  it('maps check stage to Review label', () => {
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'plan' },
      [],
      [],
      new Map(),
      [],
    )
    const checkNode = nodes.find((n) => n.stage === 'check') as TimelineStageNode
    expect(checkNode.label).toBe('Review')
  })

  it('maps done stage to Done label', () => {
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'plan' },
      [],
      [],
      new Map(),
      [],
    )
    const doneNode = nodes.find((n) => n.stage === 'done') as TimelineStageNode
    expect(doneNode.label).toBe('Done')
  })

  it('shows Approved node after plan when approval is approved', () => {
    const nodes = buildTimeline(
      {
        createdAt: '2026-04-27T10:00:00.000Z',
        stage: 'build',
        approvalState: {
          status: 'approved',
          requestedAt: '2026-04-27T10:08:00.000Z',
          approvedAt: '2026-04-27T10:08:30.000Z',
          stage: 'plan',
        },
      },
      [],
      [],
      new Map(),
      [],
    )
    const approvedNode = nodes.find((n) => n.stage === 'approved')
    expect(approvedNode).toBeDefined()
    expect(approvedNode!.label).toBe('Approved')
    if ('timestamp' in approvedNode!) {
      expect(approvedNode.timestamp).toBe('2026-04-27T10:08:30.000Z')
    }
  })

  it('does not show Approved node when not approved', () => {
    const nodes = buildTimeline(
      {
        createdAt: '2026-04-27T10:00:00.000Z',
        stage: 'build',
        approvalState: null,
      },
      [],
      [],
      new Map(),
      [],
    )
    const approvedNode = nodes.find((n) => n.stage === 'approved')
    expect(approvedNode).toBeUndefined()
  })

  it('includes task data for build stage', () => {
    const taskProgress = new Map<string, TaskProgressEntry>([
      [
        'T-001',
        { taskId: 'T-001', taskIndex: 0, totalTasks: 2, status: 'passed' },
      ],
      [
        'T-002',
        { taskId: 'T-002', taskIndex: 1, totalTasks: 2, status: 'failed', error: 'timeout' },
      ],
    ])
    const buildSession = makeSession({
      stage: 'build',
      status: 'completed',
      createdAt: '2026-04-27T10:10:00.000Z',
      completedAt: '2026-04-27T10:15:00.000Z',
    })
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'check' },
      [buildSession],
      [],
      taskProgress,
      [],
    )
    const buildNode = nodes.find((n) => n.stage === 'build') as TimelineStageNode
    expect(buildNode.tasks).toHaveLength(2)
    expect(buildNode.tasks[0].taskId).toBe('T-001')
    expect(buildNode.tasks[0].status).toBe('passed')
    expect(buildNode.tasks[1].taskId).toBe('T-002')
    expect(buildNode.tasks[1].status).toBe('failed')
    expect(buildNode.tasks[1].error).toBe('timeout')
  })

  it('shows Done as pending when not yet in done stage', () => {
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'check' },
      [],
      [],
      new Map(),
      [],
    )
    const doneNode = nodes.find((n) => n.stage === 'done') as TimelineStageNode
    expect(doneNode.status).toBe('pending')
    expect(doneNode.startedAt).toBeNull()
    expect(doneNode.completedAt).toBeNull()
    expect(doneNode.durationMs).toBeNull()
  })

  it('shows Done as completed when in done stage', () => {
    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'done' },
      [],
      [],
      new Map(),
      [],
    )
    const doneNode = nodes.find((n) => n.stage === 'done') as TimelineStageNode
    expect(doneNode.status).not.toBe('pending')
  })
})

describe('buildTimeline duration formatting helpers', () => {
  function formatDuration(ms: number): string {
    const totalSeconds = Math.floor(ms / 1000)
    const minutes = Math.floor(totalSeconds / 60)
    const seconds = totalSeconds % 60
    return `${minutes}m ${seconds}s`
  }

  it('formats 506000ms as 8m 26s', () => {
    expect(formatDuration(506000)).toBe('8m 26s')
  })

  it('formats 370000ms as 6m 10s', () => {
    expect(formatDuration(370000)).toBe('6m 10s')
  })

  it('formats 60000ms as 1m 0s', () => {
    expect(formatDuration(60000)).toBe('1m 0s')
  })

  it('formats 30000ms as 0m 30s', () => {
    expect(formatDuration(30000)).toBe('0m 30s')
  })
})

function installRAFPolyfill() {
  const callbacks: Array<{ id: number; fn: FrameRequestCallback }> = []
  let nextId = 1

  const raf = (cb: FrameRequestCallback): number => {
    const id = nextId++
    callbacks.push({ id, fn: cb })
    return id
  }

  const caf = (id: number) => {
    const idx = callbacks.findIndex((c) => c.id === id)
    if (idx >= 0) callbacks.splice(idx, 1)
  }

  const flush = () => {
    const pending = [...callbacks]
    callbacks.length = 0
    for (const { fn } of pending) fn(Date.now())
  }

  vi.stubGlobal('requestAnimationFrame', raf)
  vi.stubGlobal('cancelAnimationFrame', caf)

  return { flush }
}

describe('RAF throttling behavior', () => {
  let raf: ReturnType<typeof installRAFPolyfill>

  beforeEach(() => {
    vi.useFakeTimers()
    raf = installRAFPolyfill()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('requestAnimationFrame batches multiple calls', () => {
    const processed: number[][] = []
    let rafId: number | null = null
    const pending: number[] = []

    function scheduleFlush() {
      if (rafId !== null) return
      rafId = requestAnimationFrame(() => {
        processed.push([...pending])
        pending.length = 0
        rafId = null
      })
    }

    function pushEvent(val: number) {
      pending.push(val)
      scheduleFlush()
    }

    pushEvent(1)
    pushEvent(2)
    pushEvent(3)

    expect(processed).toHaveLength(0)

    raf.flush()

    expect(processed).toHaveLength(1)
    expect(processed[0]).toEqual([1, 2, 3])
  })

  it('batches events arriving within the same frame', () => {
    const processed: string[][] = []
    let rafId: number | null = null
    const pending: string[] = []

    function scheduleFlush() {
      if (rafId !== null) return
      rafId = requestAnimationFrame(() => {
        processed.push([...pending])
        pending.length = 0
        rafId = null
      })
    }

    for (let i = 0; i < 10; i++) {
      pending.push(`event-${i}`)
      scheduleFlush()
    }

    expect(processed).toHaveLength(0)
    raf.flush()
    expect(processed).toHaveLength(1)
    expect(processed[0]).toHaveLength(10)
  })

  it('flushes separate frames independently', () => {
    const processed: number[][] = []
    let rafId: number | null = null
    const pending: number[] = []

    function scheduleFlush() {
      if (rafId !== null) return
      rafId = requestAnimationFrame(() => {
        processed.push([...pending])
        pending.length = 0
        rafId = null
      })
    }

    pending.push(1)
    scheduleFlush()
    raf.flush()
    expect(processed).toEqual([[1]])

    pending.push(2)
    pending.push(3)
    scheduleFlush()
    raf.flush()
    expect(processed).toEqual([[1], [2, 3]])
  })

  it('throttles updates at 100ms intervals', () => {
    const processed: string[][] = []
    const pending: string[] = []
    let rafId: number | null = null
    let lastFlush = 0
    const FLUSH_INTERVAL = 100

    function scheduleFlush() {
      const now = Date.now()
      if (now - lastFlush < FLUSH_INTERVAL) return
      if (rafId !== null) return
      rafId = requestAnimationFrame(() => {
        processed.push([...pending])
        pending.length = 0
        rafId = null
        lastFlush = Date.now()
      })
    }

    pending.push('event-1')
    scheduleFlush()
    raf.flush()
    expect(processed).toEqual([['event-1']])

    pending.push('event-2')
    scheduleFlush()
    raf.flush()
    expect(processed).toEqual([['event-1']])

    vi.advanceTimersByTime(100)

    pending.push('event-3')
    scheduleFlush()
    raf.flush()
    expect(processed).toEqual([['event-1'], ['event-2', 'event-3']])
  })
})

describe('SSE event handling via buildTimeline', () => {
  it('plan_round_start and plan_round_complete produce correct plan steps', () => {
    const planSteps = [
      { roundType: 'proposal', roundLabel: 'Proposal', roundIndex: 0, status: 'completed' as const, duration: 60000, verdict: 'PASS' as const },
      { roundType: 'specs', roundLabel: 'Specs', roundIndex: 1, status: 'completed' as const, duration: 45000, verdict: 'PASS' as const },
      { roundType: 'design', roundLabel: 'Design', roundIndex: 2, status: 'running' as const },
      { roundType: 'tasks', roundLabel: 'Tasks', roundIndex: 3, status: 'pending' as const },
      { roundType: 'self-review', roundLabel: 'Self Review', roundIndex: 4, status: 'pending' as const },
    ]

    const planSession = makeSession({
      stage: 'plan',
      status: 'running',
      createdAt: '2026-04-27T10:00:00.000Z',
      completedAt: null,
    })

    const nodes = buildTimeline(
      { createdAt: '2026-04-27T09:00:00.000Z', stage: 'plan' },
      [planSession],
      [],
      new Map(),
      planSteps,
    )

    const planNode = nodes.find((n) => n.stage === 'plan') as TimelineStageNode
    expect(planNode.rounds).toHaveLength(5)
    expect(planNode.rounds[0].label).toBe('Proposal')
    expect(planNode.rounds[0].verdict).toBe('PASS')
    expect(planNode.rounds[0].duration).toBe(60000)
    expect(planNode.rounds[2].label).toBe('Design')
    expect(planNode.rounds[2].completedAt).toBeNull()
  })

  it('ralph_task_update events populate build tasks', () => {
    const taskProgress = new Map<string, TaskProgressEntry>([
      ['T-001', { taskId: 'T-001', taskIndex: 0, totalTasks: 3, status: 'passed' }],
      ['T-002', { taskId: 'T-002', taskIndex: 1, totalTasks: 3, status: 'running' }],
      ['T-003', { taskId: 'T-003', taskIndex: 2, totalTasks: 3, status: 'pending' }],
    ])

    const buildSession = makeSession({
      stage: 'build',
      status: 'running',
      createdAt: '2026-04-27T10:10:00.000Z',
      completedAt: null,
    })

    const nodes = buildTimeline(
      { createdAt: '2026-04-27T10:00:00.000Z', stage: 'build' },
      [buildSession],
      [],
      taskProgress,
      [],
    )

    const buildNode = nodes.find((n) => n.stage === 'build') as TimelineStageNode
    expect(buildNode.tasks).toHaveLength(3)
    expect(buildNode.tasks.find((t) => t.taskId === 'T-001')!.status).toBe('passed')
    expect(buildNode.tasks.find((t) => t.taskId === 'T-002')!.status).toBe('running')
    expect(buildNode.tasks.find((t) => t.taskId === 'T-003')!.status).toBe('pending')
  })
})
