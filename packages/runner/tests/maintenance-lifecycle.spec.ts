import { readFileSync } from 'node:fs'
import { describe, expect, it, vi } from 'vitest'
import { createMaintenanceLifecycle } from '../src/runtime/maintenance-lifecycle.js'
import { deferred } from './support/deferred.js'

async function flushPassCompletion(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

describe('MaintenanceLifecycle', () => {
  it('keeps cleanup single-flight ownership in the Host lifecycle', () => {
    const cleanupSource = readFileSync(new URL('../src/runtime/host-cleanup.ts', import.meta.url), 'utf8')

    expect(cleanupSource).not.toContain('cleanupInFlight')
  })

  it('orders maintenance shutdown before owned resource teardown', () => {
    const hostSource = readFileSync(new URL('../src/runtime/host.ts', import.meta.url), 'utf8')
    const maintenanceStop = hostSource.indexOf('await this.stopMaintenanceLifecycles()')

    expect(maintenanceStop).toBeGreaterThanOrEqual(0)
    for (const teardown of [
      'await this.shutdownSharedConnection()',
      'await this.taskLogDeliveryQueue.stop()',
      'await this.agentSessionRuntimeEventQueue.stop()',
      'await this.shutdownConnection()',
    ]) {
      expect(maintenanceStop).toBeLessThan(hostSource.indexOf(teardown))
    }
  })

  it('starts one pass from idle and returns it from triggerAndWait', async () => {
    vi.useFakeTimers()
    const pass = deferred()
    const operation = vi.fn(() => pass.promise)
    const lifecycle = createMaintenanceLifecycle(operation)

    const current = lifecycle.triggerAndWait()

    expect(operation).toHaveBeenCalledOnce()
    expect(lifecycle.lifecycleState).toBe('running')
    expect(lifecycle.triggerAndWait()).toBe(current)
    pass.resolve()
    await current
    await flushPassCompletion()
    expect(lifecycle.lifecycleState).toBe('idle')
  })

  it('coalesces running triggers into one pending pass', async () => {
    vi.useFakeTimers()
    const passes = [deferred(), deferred()]
    const operation = vi.fn(() => passes[operation.mock.calls.length - 1].promise)
    const lifecycle = createMaintenanceLifecycle(operation)

    lifecycle.trigger()
    lifecycle.trigger()
    lifecycle.trigger()

    expect(operation).toHaveBeenCalledOnce()
    expect(lifecycle.lifecycleState).toBe('pending')
    passes[0].resolve()
    await flushPassCompletion()
    expect(operation).toHaveBeenCalledTimes(2)
    expect(lifecycle.lifecycleState).toBe('running')
    passes[1].resolve()
    await flushPassCompletion()
    expect(lifecycle.lifecycleState).toBe('idle')
  })

  it('does not overlap a pass when the operation settles synchronously', async () => {
    vi.useFakeTimers()
    const operation = vi.fn(async () => undefined)
    const lifecycle = createMaintenanceLifecycle(operation)

    lifecycle.trigger()
    lifecycle.trigger()

    expect(operation).toHaveBeenCalledOnce()
    await flushPassCompletion()
    expect(operation).toHaveBeenCalledTimes(2)
  })

  it('registers the pass before a synchronous reentrant trigger', async () => {
    vi.useFakeTimers()
    let lifecycle!: ReturnType<typeof createMaintenanceLifecycle>
    let activePasses = 0
    let maximumActivePasses = 0
    const operation = vi.fn(() => {
      activePasses += 1
      maximumActivePasses = Math.max(maximumActivePasses, activePasses)
      if (operation.mock.calls.length === 1) lifecycle.trigger()
      activePasses -= 1
    })
    lifecycle = createMaintenanceLifecycle(operation)

    lifecycle.trigger()

    expect(operation).toHaveBeenCalledOnce()
    expect(lifecycle.lifecycleState).toBe('pending')
    await flushPassCompletion()
    expect(operation).toHaveBeenCalledTimes(2)
    expect(maximumActivePasses).toBe(1)
    expect(lifecycle.lifecycleState).toBe('idle')
  })

  it('does not overwrite a synchronous reentrant stop', async () => {
    vi.useFakeTimers()
    let lifecycle!: ReturnType<typeof createMaintenanceLifecycle>
    let stopping!: Promise<void>
    const operation = vi.fn(() => {
      stopping = lifecycle.stop()
    })
    lifecycle = createMaintenanceLifecycle(operation)

    lifecycle.trigger()

    expect(operation).toHaveBeenCalledOnce()
    expect(lifecycle.lifecycleState).toBe('stopping')
    await stopping
    expect(lifecycle.lifecycleState).toBe('stopped')
    lifecycle.trigger()
    expect(operation).toHaveBeenCalledOnce()
    await expect(lifecycle.triggerAndWait()).rejects.toThrow('stopping or stopped')
  })

  it('rejects awaitable work after stopping begins and ignores late triggers', async () => {
    vi.useFakeTimers()
    const pass = deferred()
    const lifecycle = createMaintenanceLifecycle(() => pass.promise)
    lifecycle.trigger()
    const stopping = lifecycle.stop()

    expect(lifecycle.lifecycleState).toBe('stopping')
    expect(lifecycle.trigger()).toBeUndefined()
    await expect(lifecycle.triggerAndWait()).rejects.toThrow('stopping or stopped')
    pass.resolve()
    await stopping
    expect(lifecycle.lifecycleState).toBe('stopped')
    await expect(lifecycle.triggerAndWait()).rejects.toThrow('stopping or stopped')
  })

  it('aborts the current pass and waits for its cancellation acknowledgement', async () => {
    vi.useFakeTimers()
    const cancellation = deferred()
    let signal!: AbortSignal
    const lifecycle = createMaintenanceLifecycle((passSignal) => {
      signal = passSignal
      return cancellation.promise
    })
    lifecycle.trigger()

    const stopping = lifecycle.stop()
    expect(signal.aborted).toBe(true)
    let settled = false
    void stopping.then(() => {
      settled = true
    })
    await Promise.resolve()
    expect(settled).toBe(false)

    cancellation.resolve()
    await stopping
    expect(settled).toBe(true)
    expect(lifecycle.lifecycleState).toBe('stopped')
  })

  it('stops an idle lifecycle without starting work', async () => {
    vi.useFakeTimers()
    const operation = vi.fn()
    const lifecycle = createMaintenanceLifecycle(operation)

    await lifecycle.stop()

    expect(operation).not.toHaveBeenCalled()
    expect(lifecycle.lifecycleState).toBe('stopped')
  })

  it('allows triggerAndWait to observe an operation rejection', async () => {
    vi.useFakeTimers()
    const failure = new Error('maintenance failed')
    const lifecycle = createMaintenanceLifecycle(() => Promise.reject(failure))

    await expect(lifecycle.triggerAndWait()).rejects.toBe(failure)
    await flushPassCompletion()
    expect(lifecycle.lifecycleState).toBe('idle')
  })

  it('makes stop idempotent while stopping', async () => {
    vi.useFakeTimers()
    const pass = deferred()
    const lifecycle = createMaintenanceLifecycle(() => pass.promise)
    lifecycle.trigger()

    const firstStop = lifecycle.stop()
    expect(lifecycle.stop()).toBe(firstStop)
    pass.resolve()
    await firstStop
  })
})
