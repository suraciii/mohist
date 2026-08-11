import assert from 'node:assert/strict'
import { test } from 'node:test'

import { runWithDeadline } from './deadline.js'

interface Harness {
  startResolve: (result: { exitCode: number | null }) => void
  timeoutResolve: () => void
  killCalled: boolean
  nowValue: number
}

function makeHarness(): Harness {
  const harness: Harness = { startResolve: () => {}, timeoutResolve: () => {}, killCalled: false, nowValue: 1000 }
  return harness
}

function deps(harness: Harness, startPromise: Promise<{ exitCode: number | null }>, timeoutPromise: Promise<void>) {
  return {
    start: () => startPromise,
    kill: async () => {
      harness.killCalled = true
    },
    timeout: timeoutPromise,
    now: () => harness.nowValue,
  }
}

test('runWithDeadline passes when the child exits 0 before the deadline', async () => {
  const harness = makeHarness()
  let startRelease!: (r: { exitCode: number | null }) => void
  const startPromise = new Promise<{ exitCode: number | null }>((res) => (startRelease = res))
  const timeoutPromise = new Promise<void>(() => {}) // never fires
  const pending = runWithDeadline(deps(harness, startPromise, timeoutPromise))
  harness.nowValue = 1000
  startRelease({ exitCode: 0 })
  harness.nowValue = 1200
  const outcome = await pending
  assert.equal(outcome.status, 'passed')
  assert.equal(outcome.exitCode, 0)
  assert.equal(outcome.elapsedMs, 200)
  assert.equal(harness.killCalled, false)
})

test('runWithDeadline reports failed on non-zero exit without killing', async () => {
  const harness = makeHarness()
  let startRelease!: (r: { exitCode: number | null }) => void
  const startPromise = new Promise<{ exitCode: number | null }>((res) => (startRelease = res))
  const timeoutPromise = new Promise<void>(() => {})
  const pending = runWithDeadline(deps(harness, startPromise, timeoutPromise))
  startRelease({ exitCode: 3 })
  harness.nowValue = 1300
  const outcome = await pending
  assert.equal(outcome.status, 'failed')
  assert.equal(outcome.exitCode, 3)
  assert.equal(harness.killCalled, false)
})

test('runWithDeadline times out: kills the child and reports timeout with elapsed time', async () => {
  const harness = makeHarness()
  const startPromise = new Promise<{ exitCode: number | null }>(() => {}) // child never exits on its own
  let timeoutRelease!: () => void
  const timeoutPromise = new Promise<void>((res) => (timeoutRelease = res))
  const pending = runWithDeadline(deps(harness, startPromise, timeoutPromise))
  harness.nowValue = 1000
  timeoutRelease()
  harness.nowValue = 5000
  const outcome = await pending
  assert.equal(outcome.status, 'timeout')
  assert.equal(outcome.exitCode, null)
  assert.equal(outcome.elapsedMs, 4000)
  assert.equal(harness.killCalled, true)
})

test('runWithDeadline waits for suite-timeout child cleanup before returning', async () => {
  const harness = makeHarness()
  const startPromise = new Promise<{ exitCode: number | null }>(() => {})
  let suiteTimeout!: () => void
  const timeoutPromise = new Promise<'suite'>((res) => (suiteTimeout = () => res('suite')))
  let cleanupRelease!: () => void
  let cleanupStarted!: () => void
  const cleanupStartedPromise = new Promise<void>((res) => (cleanupStarted = res))
  let cleanupCalls = 0
  const pending = runWithDeadline({
    start: () => startPromise,
    kill: () => {
      cleanupCalls += 1
      cleanupStarted()
      return new Promise<void>((res) => (cleanupRelease = res))
    },
    timeout: timeoutPromise,
    now: () => harness.nowValue,
  })

  suiteTimeout()
  await cleanupStartedPromise

  let settled = false
  void pending.then(() => (settled = true))
  assert.equal(settled, false)
  assert.equal(cleanupCalls, 1)

  cleanupRelease()
  const outcome = await pending
  assert.equal(outcome.status, 'timeout')
  assert.equal(outcome.exitCode, null)
  assert.equal(outcome.timeoutReason, 'suite')
  assert.equal(cleanupCalls, 1)
})

test('runWithDeadline reports only the deadline outcome after bounded best-effort cleanup', async () => {
  const timeout = Promise.resolve('track' as const)
  let cleanupCalls = 0
  const outcome = await runWithDeadline({
    start: () => new Promise<{ exitCode: number | null }>(() => undefined),
    kill: async () => {
      cleanupCalls += 1
    },
    timeout,
    now: () => 100,
  })

  assert.deepEqual(outcome, {
    status: 'timeout',
    exitCode: null,
    elapsedMs: 0,
    timeoutReason: 'track',
  })
  assert.equal(cleanupCalls, 1)
  assert.equal('cleanupFailed' in outcome, false)
  assert.equal('cleanupError' in outcome, false)
})
