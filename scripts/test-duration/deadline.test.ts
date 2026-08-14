import assert from 'node:assert/strict'
import { test } from 'node:test'

import { externalAbortCleanupDeadlineAt, runWithDeadline, suiteDeadlines, suiteDeadlinesAt } from './deadline.js'

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

test('runWithDeadline treats a child completion at the hard cutoff as timeout and converges it', async () => {
  const harness = makeHarness()
  let startRelease!: (result: { exitCode: number | null }) => void
  const startPromise = new Promise<{ exitCode: number | null }>((resolvePromise) => {
    startRelease = resolvePromise
  })
  const pending = runWithDeadline({
    ...deps(harness, startPromise, new Promise<void>(() => {})),
    hardDeadlineAt: 1_200,
  })

  startRelease({ exitCode: 0 })
  harness.nowValue = 1_200

  const outcome = await pending
  assert.equal(outcome.status, 'timeout')
  assert.equal(outcome.exitCode, null)
  assert.equal(harness.killCalled, true)
})

test('suite deadlines reserve cleanup and finalization inside one absolute five-minute wall', () => {
  const deadlines = suiteDeadlines(1_000, 300_000, 5_000)

  assert.deepEqual(deadlines, { hardDeadlineAt: 301_000, executionDeadlineAt: 290_000 })
  assert.deepEqual(suiteDeadlinesAt(301_000, 5_000), deadlines)
  assert.throws(() => suiteDeadlines(1_000, 11_000, 5_000), /cleanup and finalization reserve/)
})

test('external abort cleanup leaves margin before the outer KILL grace while respecting the internal hard wall', () => {
  assert.equal(externalAbortCleanupDeadlineAt(270_000, 300_000, 5_000), 276_000)
  assert.equal(externalAbortCleanupDeadlineAt(298_000, 300_000, 5_000), 300_000)
})
