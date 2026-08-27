import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createHostShutdown } from '../src/runtime/host-update-shutdown.js'
import type { InFlightEntry } from '../src/runtime/host-state.js'

function entry(workId: string, done: Promise<void> = new Promise<void>(() => undefined)): InFlightEntry {
  return {
    work: {
      workflowRunId: 'workflow-1',
      workId,
      workType: 'task',
      ownerKind: 'workflow',
    },
    controller: new AbortController(),
    done,
  }
}

function makeHost(entries: InFlightEntry[], stopBudgetMs = 100) {
  const inFlight = new Map(entries.map((value) => [`workflow:workflow-1:${value.work.workId}`, value]))
  return {
    inFlight,
    shutdown: createHostShutdown({ inFlight, shutdownStopBudgetMs: stopBudgetMs }),
  }
}

describe('createHostShutdown', () => {
  beforeEach(() => vi.useFakeTimers())

  it('aborts in-flight work and removes it after the bounded shutdown wait', async () => {
    const running = entry('work-1')
    const { shutdown, inFlight } = makeHost([running])

    const stopping = shutdown.shutdownInFlight()
    await vi.runAllTimersAsync()
    await stopping

    expect(running.controller.signal.aborted).toBe(true)
    expect(running.shutdown).toEqual({ requested: true, stopConfirmed: false, operationId: null })
    expect(inFlight.size).toBe(0)
  })

  it('waits for work that settles inside the shutdown budget', async () => {
    let settle!: () => void
    const running = entry('work-1', new Promise<void>((resolve) => (settle = resolve)))
    const { shutdown, inFlight } = makeHost([running])

    const stopping = shutdown.shutdownInFlight()
    settle()
    await stopping

    expect(inFlight.size).toBe(0)
  })
})
