import assert from 'node:assert/strict'
import { test } from 'node:test'

import { scheduleLanes, type RunningLane } from './scheduler.js'

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

interface StartSignals {
  readonly started: readonly string[]
  readonly record: (id: string) => void
  readonly waitFor: (id: string) => Promise<void>
}

function startSignals(...ids: readonly string[]): StartSignals {
  const started: string[] = []
  const signals = new Map(ids.map((id) => [id, deferred<void>()]))

  return {
    started,
    record: (id) => {
      started.push(id)
      signals.get(id)?.resolve(undefined)
    },
    waitFor: (id) => {
      const signal = signals.get(id)
      if (!signal) throw new Error(`unexpected started lane: ${id}`)
      return signal.promise
    },
  }
}

test('scheduler admits only resource-compatible lanes and releases a completed claim', async () => {
  const starts = startSignals('first', 'node', 'second')
  const first = deferred<boolean>()
  const node = deferred<boolean>()
  const second = deferred<boolean>()
  const deferredById = new Map([
    ['first', first],
    ['node', node],
    ['second', second],
  ])

  const pending = scheduleLanes(
    [
      { id: 'first', resources: ['host', 'dotnet'] },
      { id: 'second', resources: ['host', 'dotnet'] },
      { id: 'node', resources: ['host', 'node'] },
    ],
    (lane): RunningLane<boolean> => {
      starts.record(lane.id)
      return { result: deferredById.get(lane.id)!.promise, cancel: () => {} }
    },
    (result) => result,
    { resourceLimits: { host: 2, dotnet: 1, node: 1 } },
  )

  await Promise.all([starts.waitFor('first'), starts.waitFor('node')])
  assert.deepEqual(starts.started, ['first', 'node'])
  first.resolve(true)
  await starts.waitFor('second')
  assert.deepEqual(starts.started, ['first', 'node', 'second'])
  node.resolve(true)
  second.resolve(true)

  const result = await pending
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['passed', 'passed', 'passed'],
  )
})

test('scheduler treats a weighted inner test-runner claim as one resource budget', async () => {
  const starts = startSignals('wide', 'queued')
  const wide = deferred<boolean>()
  const queued = deferred<boolean>()
  const pending = scheduleLanes(
    [
      { id: 'wide', resources: ['host', 'server-spec'], resourceWeights: { 'server-spec': 2 } },
      { id: 'queued', resources: ['host', 'server-spec'], resourceWeights: { 'server-spec': 2 } },
    ],
    (lane): RunningLane<boolean> => {
      starts.record(lane.id)
      return { result: lane.id === 'wide' ? wide.promise : queued.promise, cancel: () => {} }
    },
    (result) => result,
    { resourceLimits: { host: 2, 'server-spec': 2 } },
  )

  await starts.waitFor('wide')
  assert.deepEqual(starts.started, ['wide'])
  wide.resolve(true)
  await starts.waitFor('queued')
  queued.resolve(true)
  const result = await pending
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['passed', 'passed'],
  )
})

test('scheduler cancels active lanes and does not admit queued lanes after the first failure', async () => {
  const starts = startSignals('first', 'second', 'queued')
  const cancelled: string[] = []
  const first = deferred<boolean>()
  const second = deferred<boolean>()
  const deferredById = new Map([
    ['first', first],
    ['second', second],
  ])

  const pending = scheduleLanes(
    [
      { id: 'first', resources: ['host'] },
      { id: 'second', resources: ['host'] },
      { id: 'queued', resources: ['host'] },
    ],
    (lane): RunningLane<boolean> => {
      starts.record(lane.id)
      return {
        result: deferredById.get(lane.id)!.promise,
        cancel: () => {
          cancelled.push(lane.id)
          deferredById.get(lane.id)!.resolve(false)
        },
      }
    },
    (result) => result,
    { resourceLimits: { host: 2 } },
  )

  await Promise.all([starts.waitFor('first'), starts.waitFor('second')])
  assert.deepEqual(starts.started, ['first', 'second'])
  first.resolve(false)

  const result = await pending
  assert.deepEqual(starts.started, ['first', 'second'])
  assert.deepEqual(cancelled, ['second'])
  assert.equal(result.failureLaneId, 'first')
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['failed', 'cancelled', 'cancelled'],
  )
})

test('scheduler turns a rejected lane into a failure and converges active cancellation', async () => {
  const active = deferred<boolean>()
  const cancelled: string[] = []

  const result = await scheduleLanes(
    [
      { id: 'rejected', resources: ['host'] },
      { id: 'active', resources: ['host'] },
    ],
    (lane): RunningLane<boolean> => {
      if (lane.id === 'rejected') {
        return { result: Promise.reject(new Error('lane crashed')), cancel: () => {} }
      }
      return {
        result: active.promise,
        cancel: () => {
          cancelled.push(lane.id)
          active.resolve(false)
        },
      }
    },
    (value) => value,
    { resourceLimits: { host: 2 } },
  )

  assert.equal(result.failureLaneId, 'rejected')
  assert.deepEqual(cancelled, ['active'])
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['failed', 'cancelled'],
  )
})

test('scheduler waits for all partition dependencies before admitting the coverage lane', async () => {
  const starts = startSignals('spec-0', 'spec-1', 'spec-coverage')
  const first = deferred<boolean>()
  const second = deferred<boolean>()
  const coverage = deferred<boolean>()
  const firstCompletionObserved = deferred<void>()
  const deferredById = new Map([
    ['spec-0', first],
    ['spec-1', second],
    ['spec-coverage', coverage],
  ])

  const pending = scheduleLanes(
    [
      { id: 'spec-0', resources: ['host', 'spec-0-report', 'spec-0-temp', 'spec-0-port'] },
      { id: 'spec-1', resources: ['host', 'spec-1-report', 'spec-1-temp', 'spec-1-port'] },
      { id: 'spec-coverage', dependsOn: ['spec-0', 'spec-1'], resources: ['host'] },
    ],
    (lane): RunningLane<boolean> => {
      starts.record(lane.id)
      return { result: deferredById.get(lane.id)!.promise, cancel: () => {} }
    },
    (result) => {
      if (result) firstCompletionObserved.resolve(undefined)
      return result
    },
    { resourceLimits: { host: 2 } },
  )

  await Promise.all([starts.waitFor('spec-0'), starts.waitFor('spec-1')])
  assert.deepEqual(starts.started, ['spec-0', 'spec-1'])
  first.resolve(true)
  await firstCompletionObserved.promise
  assert.deepEqual(starts.started, ['spec-0', 'spec-1'])
  second.resolve(true)
  await starts.waitFor('spec-coverage')
  assert.deepEqual(starts.started, ['spec-0', 'spec-1', 'spec-coverage'])
  coverage.resolve(true)

  const result = await pending
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['passed', 'passed', 'passed'],
  )
})

test('scheduler completes duration measurements before admitting bounded throughput fan-out', async () => {
  const starts = startSignals('cli', 'server-unit', 'web', 'server-spec')
  const cli = deferred<boolean>()
  const unit = deferred<boolean>()
  const web = deferred<boolean>()
  const spec = deferred<boolean>()
  const deferredById = new Map([
    ['cli', cli],
    ['server-unit', unit],
    ['web', web],
    ['server-spec', spec],
  ])

  const pending = scheduleLanes(
    [
      { id: 'cli', resources: ['host', 'dotnet', 'duration-measurement'] },
      { id: 'server-unit', dependsOn: ['cli'], resources: ['host', 'dotnet', 'duration-measurement'] },
      { id: 'web', dependsOn: ['server-unit'], resources: ['host', 'node'] },
      { id: 'server-spec', dependsOn: ['server-unit'], resources: ['host', 'dotnet'] },
    ],
    (lane): RunningLane<boolean> => {
      starts.record(lane.id)
      return { result: deferredById.get(lane.id)!.promise, cancel: () => {} }
    },
    (result) => result,
    { resourceLimits: { host: 2, dotnet: 1, node: 1, 'duration-measurement': 1 } },
  )

  await starts.waitFor('cli')
  assert.deepEqual(starts.started, ['cli'])
  cli.resolve(true)
  await starts.waitFor('server-unit')
  assert.deepEqual(starts.started, ['cli', 'server-unit'])
  unit.resolve(true)
  await Promise.all([starts.waitFor('web'), starts.waitFor('server-spec')])
  assert.deepEqual(starts.started, ['cli', 'server-unit', 'web', 'server-spec'])
  web.resolve(true)
  spec.resolve(true)

  const result = await pending
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['passed', 'passed', 'passed', 'passed'],
  )
})

test('scheduler admits no lane when the abort signal was already fulfilled', async () => {
  const abort = new AbortController()
  abort.abort()
  const started: string[] = []

  const result = await scheduleLanes(
    [{ id: 'never-started', resources: ['host'] }],
    (lane): RunningLane<boolean> => {
      started.push(lane.id)
      return { result: Promise.resolve(true), cancel: () => {} }
    },
    (value) => value,
    { resourceLimits: { host: 1 }, abort: abort.signal },
  )

  assert.deepEqual(started, [])
  assert.equal(result.aborted, true)
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['cancelled'],
  )
})

test('scheduler waits for every active lane cancellation to converge after an external abort', async () => {
  const abort = new AbortController()
  const starts = startSignals('active')
  const activeResult = deferred<boolean>()
  const cleanup = deferred<void>()
  const cleanupStarted = deferred<void>()
  let cancelCalls = 0

  const pending = scheduleLanes(
    [
      { id: 'active', resources: ['host'] },
      { id: 'queued', resources: ['host'] },
    ],
    (lane): RunningLane<boolean> => {
      starts.record(lane.id)
      if (lane.id === 'queued') return { result: Promise.resolve(true), cancel: () => {} }
      return {
        result: activeResult.promise,
        cancel: () => {
          cancelCalls += 1
          cleanupStarted.resolve(undefined)
          return cleanup.promise.then(() => activeResult.resolve(false))
        },
      }
    },
    (result) => result,
    { resourceLimits: { host: 1 }, abort: abort.signal },
  )

  await starts.waitFor('active')
  abort.abort()
  await cleanupStarted.promise

  let settled = false
  void pending.then(() => {
    settled = true
  })
  await Promise.resolve()
  assert.equal(settled, false)
  assert.equal(cancelCalls, 1)
  assert.deepEqual(starts.started, ['active'])

  cleanup.resolve(undefined)
  const result = await pending
  assert.equal(result.aborted, true)
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['cancelled', 'cancelled'],
  )
})

test('scheduler records cancellation callbacks that throw without losing the final lane report', async () => {
  const abort = new AbortController()
  const started = deferred<void>()
  const resultPromise = scheduleLanes(
    [{ id: 'active', resources: ['host'] }],
    () => ({
      result: started.promise.then(() => true),
      cancel: () => {
        throw new Error('cancel seam failed')
      },
    }),
    (value) => value,
    { resourceLimits: { host: 1 }, abort: abort.signal },
  )

  await Promise.resolve()
  abort.abort()
  started.resolve(undefined)
  const result = await resultPromise

  assert.equal(result.aborted, true)
  assert.deepEqual(
    result.lanes.map((lane) => lane.state),
    ['cancelled'],
  )
})
