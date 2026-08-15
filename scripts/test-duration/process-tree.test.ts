import assert from 'node:assert/strict'
import { test } from 'node:test'

import { terminateProcessTree, type ProcessTreeOps } from './process-tree.js'

interface Deferred<T> {
  readonly promise: Promise<T>
  readonly resolve: (value: T) => void
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise
  })
  return { promise, resolve }
}

function manualClock(initialNow = 0): {
  readonly now: () => number
  readonly createTimeout: ProcessTreeOps['createTimeout']
  readonly advanceTo: (nextNow: number) => void
} {
  let now = initialNow
  let nextId = 0
  const timers = new Map<number, { readonly dueAt: number; readonly resolve: () => void }>()
  return {
    now: () => now,
    createTimeout: (delayMs) => {
      const id = nextId++
      let resolvePromise!: () => void
      const promise = new Promise<void>((resolve) => {
        resolvePromise = resolve
      })
      timers.set(id, { dueAt: now + delayMs, resolve: resolvePromise })
      return {
        promise,
        cancel: () => {
          timers.delete(id)
        },
      }
    },
    advanceTo: (nextNow) => {
      now = nextNow
      for (const [id, timer] of timers) {
        if (timer.dueAt <= now) {
          timers.delete(id)
          timer.resolve()
        }
      }
    },
  }
}

test('Windows tree cancellation invokes taskkill /T and waits for the launched tree terminal event', async () => {
  const clock = manualClock()
  const childDone = deferred<void>()
  const taskkillDone = deferred<{ exitCode: number | null }>()
  const taskkillPids: number[] = []
  let taskkillCancelled = false
  const ops: ProcessTreeOps = {
    platform: 'win32',
    now: clock.now,
    createTimeout: clock.createTimeout,
    signalProcessGroup: () => assert.fail('Windows must not use a POSIX process group'),
    isProcessGroupAlive: () => assert.fail('Windows must not inspect a POSIX process group'),
    startTaskkill: (pid) => {
      taskkillPids.push(pid)
      return {
        done: taskkillDone.promise,
        cancel: () => {
          taskkillCancelled = true
        },
      }
    },
  }

  const terminating = terminateProcessTree({ pid: 77, done: childDone.promise }, 100, 5, ops)
  assert.deepEqual(taskkillPids, [77])
  taskkillDone.resolve({ exitCode: 0 })
  await Promise.resolve()
  childDone.resolve(undefined)

  assert.equal(await terminating, true)
  assert.equal(taskkillCancelled, false)
})

test('Windows taskkill cleanup is bounded by the supplied absolute deadline', async () => {
  const clock = manualClock()
  const taskkillDone = deferred<{ exitCode: number | null }>()
  let cancelled = false
  const ops: ProcessTreeOps = {
    platform: 'win32',
    now: clock.now,
    createTimeout: clock.createTimeout,
    signalProcessGroup: () => assert.fail('Windows must not use a POSIX process group'),
    isProcessGroupAlive: () => assert.fail('Windows must not inspect a POSIX process group'),
    startTaskkill: () => ({
      done: taskkillDone.promise,
      cancel: () => {
        cancelled = true
      },
    }),
  }

  const terminating = terminateProcessTree({ pid: 78, done: deferred<void>().promise }, 20, 5, ops)
  clock.advanceTo(20)

  assert.equal(await terminating, false)
  assert.equal(cancelled, true)
})

test('Windows cleanup fails closed when taskkill exits nonzero even after the root exits', async () => {
  const clock = manualClock()
  const childDone = deferred<void>()
  const ops: ProcessTreeOps = {
    platform: 'win32',
    now: clock.now,
    createTimeout: clock.createTimeout,
    signalProcessGroup: () => assert.fail('Windows must not use a POSIX process group'),
    isProcessGroupAlive: () => assert.fail('Windows must not inspect a POSIX process group'),
    startTaskkill: () => ({ done: Promise.resolve({ exitCode: 5 }), cancel: () => {} }),
  }

  const terminating = terminateProcessTree({ pid: 83, done: childDone.promise }, 100, 5, ops)
  childDone.resolve(undefined)

  assert.equal(await terminating, false)
})

test('POSIX cleanup escalates TERM to KILL and still waits only to the absolute deadline', async () => {
  const clock = manualClock()
  const childDone = deferred<void>()
  const killSent = deferred<void>()
  const signals: string[] = []
  let groupAlive = true
  const ops: ProcessTreeOps = {
    platform: 'linux',
    now: clock.now,
    createTimeout: clock.createTimeout,
    signalProcessGroup: (pid, signal) => {
      signals.push(`${pid}:${signal}`)
      if (signal === 'SIGKILL') {
        groupAlive = false
        killSent.resolve(undefined)
      }
    },
    isProcessGroupAlive: () => groupAlive,
    startTaskkill: () => assert.fail('POSIX must not start taskkill'),
  }

  const terminating = terminateProcessTree({ pid: 79, done: childDone.promise }, 20, 5, ops)
  assert.deepEqual(signals, ['79:SIGTERM'])
  clock.advanceTo(5)
  await killSent.promise
  assert.deepEqual(signals, ['79:SIGTERM', '79:SIGKILL'])
  childDone.resolve(undefined)

  assert.equal(await terminating, true)
})

test('POSIX cleanup does not treat an exited leader as a vanished process group', async () => {
  const clock = manualClock()
  const childDone = deferred<void>()
  const killSent = deferred<void>()
  const signals: string[] = []
  let groupAlive = true
  const ops: ProcessTreeOps = {
    platform: 'linux',
    now: clock.now,
    createTimeout: clock.createTimeout,
    signalProcessGroup: (pid, signal) => {
      signals.push(`${pid}:${signal}`)
      if (signal === 'SIGKILL') {
        groupAlive = false
        killSent.resolve(undefined)
      }
    },
    isProcessGroupAlive: () => groupAlive,
    startTaskkill: () => assert.fail('POSIX must not start taskkill'),
  }

  const terminating = terminateProcessTree({ pid: 81, done: childDone.promise }, 20, 5, ops)
  childDone.resolve(undefined)
  await Promise.resolve()
  assert.deepEqual(signals, ['81:SIGTERM'])
  assert.equal(groupAlive, true)

  clock.advanceTo(5)
  await killSent.promise
  assert.deepEqual(signals, ['81:SIGTERM', '81:SIGKILL'])
  assert.equal(await terminating, true)
})

test('POSIX cleanup fails when the process group remains after KILL', async () => {
  const clock = manualClock()
  const childDone = deferred<void>()
  const signals: string[] = []
  const ops: ProcessTreeOps = {
    platform: 'linux',
    now: clock.now,
    createTimeout: clock.createTimeout,
    signalProcessGroup: (pid, signal) => {
      signals.push(`${pid}:${signal}`)
    },
    isProcessGroupAlive: () => true,
    startTaskkill: () => assert.fail('POSIX must not start taskkill'),
  }

  const terminating = terminateProcessTree({ pid: 82, done: childDone.promise }, 20, 5, ops)
  childDone.resolve(undefined)
  await Promise.resolve()
  clock.advanceTo(5)

  assert.equal(await terminating, false)
  assert.deepEqual(signals, ['82:SIGTERM', '82:SIGKILL'])
})

test('a terminal event observed at the absolute cutoff does not satisfy process-tree convergence', async () => {
  const clock = manualClock()
  const childDone = deferred<void>()
  const ops: ProcessTreeOps = {
    platform: 'win32',
    now: clock.now,
    createTimeout: clock.createTimeout,
    signalProcessGroup: () => assert.fail('Windows must not use a POSIX process group'),
    isProcessGroupAlive: () => assert.fail('Windows must not inspect a POSIX process group'),
    startTaskkill: () => ({ done: Promise.resolve({ exitCode: 0 }), cancel: () => {} }),
  }

  const terminating = terminateProcessTree({ pid: 80, done: childDone.promise }, 20, 5, ops)
  await Promise.resolve()
  childDone.resolve(undefined)
  clock.advanceTo(20)

  assert.equal(await terminating, false)
})
