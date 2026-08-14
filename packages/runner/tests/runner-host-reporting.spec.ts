import { AsyncLocalStorage } from 'node:async_hooks'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { RunnerHost } from '../src/runtime/host.js'
import { WorkExecutor } from '../src/runtime/executor.js'
import type { SessionTarget } from '../src/server/session-target.js'
import { deferred } from './support/deferred.js'
import { capturedLogs, onCapturedLog } from './support/logger-test.js'
import { withDefaultRunnerTestResources, type DefaultRunnerTestResources } from './support/test-resources.js'

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000
const AWAITING_ACK_RETRY_INTERVAL_MS = 5_000

type ReportingMock = ReturnType<typeof vi.fn>
type ReportingMocks = Record<
  | 'connect'
  | 'heartbeat'
  | 'disconnect'
  | 'poll'
  | 'report'
  | 'uploadTaskLog'
  | 'fetchConfig'
  | 'startSignalR'
  | 'stopSignalR'
  | 'getConnectionId'
  | 'probeLiveness'
  | 'blockingAction'
  | 'forceReconnect',
  ReportingMock
>

interface ReportingTestState {
  readonly resources: DefaultRunnerTestResources
  readonly mocks: ReportingMocks
  onReconnected: ((connectionId: string) => void) | null
  followupTargetResolver:
    | ((target: SessionTarget) => { runtimeSessionId: string; workDir: string; projectId: string } | null)
    | null
}

const reportingTestStorage = new AsyncLocalStorage<ReportingTestState>()

function currentReportingTestState(): ReportingTestState {
  const state = reportingTestStorage.getStore()
  if (!state) throw new Error('runner host reporting test context is not active')
  return state
}

function scopedMock(name: keyof ReportingMocks): ReportingMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, '_isMockFunction', { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentReportingTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentReportingTestState().mocks[name], property)
      return typeof value === 'function' ? value.bind(currentReportingTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentReportingTestState().mocks[name], property, value)
    },
  }) as unknown as ReportingMock
}

const connect = scopedMock('connect')
const heartbeat = scopedMock('heartbeat')
const disconnect = scopedMock('disconnect')
const poll = scopedMock('poll')
const report = scopedMock('report')
const uploadTaskLog = scopedMock('uploadTaskLog')
const fetchConfig = scopedMock('fetchConfig')
const startSignalR = scopedMock('startSignalR')
const stopSignalR = scopedMock('stopSignalR')
const getConnectionId = scopedMock('getConnectionId')
const probeLiveness = scopedMock('probeLiveness')
const blockingAction = scopedMock('blockingAction')
const forceReconnect = scopedMock('forceReconnect')

function createReportingMocks(): ReportingMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    report: vi.fn(async () => ({ tracked: true })),
    uploadTaskLog: vi.fn(async () => ({ status: 'changed', accepted: 0, truncated: false })),
    fetchConfig: vi.fn(async () => null),
    startSignalR: vi.fn(async () => undefined),
    stopSignalR: vi.fn(async () => undefined),
    getConnectionId: vi.fn(() => 'conn-1'),
    probeLiveness: vi.fn(async () => true),
    blockingAction: vi.fn(async ({ signal }: { signal: AbortSignal }) => {
      const aborted = deferred<{ error: { code: string; message: string } }>()
      if (signal.aborted) {
        aborted.resolve({ error: { code: 'action-failed', message: 'aborted' } })
      } else {
        signal.addEventListener(
          'abort',
          () => aborted.resolve({ error: { code: 'action-failed', message: 'aborted' } }),
          { once: true },
        )
      }
      return aborted.promise
    }),
    forceReconnect: vi.fn(async () => undefined),
  }
}

vi.mock('../src/server/connection.js', () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
    report = report
    uploadTaskLog = uploadTaskLog
    fetchConfig = fetchConfig
  },
}))

vi.mock('../src/server/runner-signalr.js', () => ({
  RunnerSignalRClient: class {
    start = startSignalR
    stop = stopSignalR
    getConnectionId = getConnectionId
    probeLiveness = probeLiveness
    forceReconnect = forceReconnect
    constructor(
      _serverUrl: string,
      _runnerId: string,
      _runnerRoot: string,
      _buildGitHash: string | null,
      options: {
        onReconnected?: (id: string) => void
        followupTargetResolver?: (
          target: SessionTarget,
        ) => { runtimeSessionId: string; workDir: string; projectId: string } | null
      } = {},
    ) {
      currentReportingTestState().onReconnected = options.onReconnected ?? null
      currentReportingTestState().followupTargetResolver = options.followupTargetResolver ?? null
    }
  },
}))

vi.mock('../src/actions/registry.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/actions/registry.js')>()
  return {
    ...actual,
    createDefaultRegistry: () =>
      new actual.ActionRegistry([
        {
          manifest: {
            name: 'test/block',
            inputs: {},
            outputs: [],
            errors: [{ code: 'action-failed', description: 'The test Action failed' }],
          },
          run: blockingAction as never,
        },
      ]),
  }
})

function it(name: string, body: () => Promise<void> | void): void {
  vitestIt(name, async () => {
    await withDefaultRunnerTestResources(async (resources) => {
      const state: ReportingTestState = {
        resources,
        mocks: createReportingMocks(),
        onReconnected: null,
        followupTargetResolver: null,
      }
      await reportingTestStorage.run(state, async () => {
        vi.useFakeTimers()
        try {
          await body()
        } finally {
          vi.useRealTimers()
        }
      })
    })
  })
}

describe('RunnerHost', () => {
  it('PollBody_CarriesInFlightAndAwaitingAck_Keys', async () => {
    const reportStarted = deferred<void>()
    const reportRelease = deferred<void>()
    const secondPollStarted = deferred<void>()
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    report.mockImplementation(async () => {
      reportStarted.resolve()
      await reportRelease.promise
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const held = {
      workflowRunId: 'wr-held',
      workId: 'work-held',
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollIndex += 1
      if (pollIndex === 1) return [held]
      secondPollStarted.resolve()
      return []
    })
    blockingAction.mockResolvedValue({ output: { message: 'ok' } })
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const run = host.run(controller.signal)

    try {
      await reportStarted.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await secondPollStarted.promise

      const bodies = poll.mock.calls
        .filter((calls) => calls.length > 1 && calls[1])
        .map((calls) => calls[1] as { inFlight: string[]; awaitingAck: string[] })
      expect(bodies.some((body) => body.awaitingAck.includes('workflow:wr-held:work-held'))).toBe(true)

      controller.abort()
      reportRelease.resolve()
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      reportRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it('ReDispatchedWork_ReportedOnce_NotPerRedelivery', async () => {
    const reportStarted = deferred<void>()
    const reportRelease = deferred<void>()
    const pollCalls = [deferred<void>(), deferred<void>(), deferred<void>(), deferred<void>()]
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    report.mockImplementation(async () => {
      reportStarted.resolve()
      await reportRelease.promise
      return {}
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const same = {
      workflowRunId: 'wr-dup',
      workId: 'work-dup',
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollCalls[pollIndex]?.resolve()
      pollIndex += 1
      return pollIndex <= 3 ? [same] : []
    })
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    blockingAction.mockResolvedValue({ error: { code: 'action-failed', message: 'runtime turn failed' }, exitCode: 1 })
    const run = host.run(controller.signal)

    try {
      await reportStarted.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[1]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[2]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[3]!.promise

      const reportsForDup = report.mock.calls.filter((calls) => calls[0]?.workId === 'work-dup')
      expect(reportsForDup.length).toBeLessThanOrEqual(1)

      controller.abort()
      reportRelease.resolve()
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      reportRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it('AwaitingAck_RetriesReportUntilAcked', async () => {
    const firstReport = deferred<void>()
    const secondReport = deferred<void>()
    const thirdReport = deferred<void>()
    const firstFailureLogged = deferred<void>()
    const secondFailureLogged = deferred<void>()
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    const firstFailure = new Error('first transient')
    const secondFailure = new Error('second transient')
    let attempt = 0
    report.mockImplementation(async () => {
      attempt += 1
      if (attempt === 1) {
        firstReport.resolve()
        throw firstFailure
      }
      if (attempt === 2) {
        secondReport.resolve()
        throw secondFailure
      }
      thirdReport.resolve()
      return { tracked: true }
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const work = {
      workflowRunId: 'wr-retry',
      workId: 'work-retry',
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    poll.mockResolvedValueOnce([work]).mockResolvedValue([])
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    blockingAction.mockResolvedValue({ output: { message: 'ok' } })
    const stopLog = onCapturedLog((record) => {
      if (record.message === 'first work report failed; will retry') firstFailureLogged.resolve()
      if (record.message === 'work report retry failed') secondFailureLogged.resolve()
    })
    const run = host.run(controller.signal)
    try {
      await firstReport.promise
      await firstFailureLogged.promise
      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS)
      await secondReport.promise
      await secondFailureLogged.promise
      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS)
      await thirdReport.promise

      expect(report.mock.calls.map((calls) => calls[1]?.status)).toEqual(['failed', 'failed', 'failed'])

      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(uploadTaskLog).toHaveBeenCalledTimes(1)
      expect(capturedLogs()).toEqual(
        expect.arrayContaining([
          expect.objectContaining({
            level: 'WARN',
            message: 'first work report failed; will retry',
            fields: expect.objectContaining({ work: 'work-retry', exception: firstFailure }),
          }),
          expect.objectContaining({
            level: 'WARN',
            message: 'work report retry failed',
            fields: expect.objectContaining({ work: 'work-retry', attempt: 2, exception: secondFailure }),
          }),
        ]),
      )
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      stopLog()
    }
  })

  it('UnknownResult_RetainsItsOriginalWorkUntilTheServerDurablyAcknowledgesIt', async () => {
    const firstReport = deferred<void>()
    const firstFailureLogged = deferred<void>()
    const replayedReport = deferred<void>()
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    let reportAttempt = 0
    const reportedStatuses: string[] = []
    report.mockImplementation(async (_work, result: { status: string }) => {
      reportAttempt += 1
      reportedStatuses.push(result.status)
      if (reportAttempt === 1) {
        firstReport.resolve()
        return { tracked: false, reason: 'observation-not-durable' }
      }
      replayedReport.resolve()
      return { tracked: true, reason: 'accepted' }
    })
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    const controller = new AbortController()
    const work = {
      workflowRunId: 'wr-unknown-replay',
      workId: 'work-unknown-replay',
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    poll.mockResolvedValueOnce([work]).mockResolvedValue([])
    const executeWithLog = vi
      .spyOn(WorkExecutor.prototype, 'executeWithLog')
      .mockImplementation(async (_work, _signal, collector) => ({
        result: {
          status: 'unknown',
          message: 'Agent cleanup was not confirmed',
          error: { code: 'timeout', message: 'Agent cleanup was not confirmed' },
        },
        collector: collector!,
      }))
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const stopLog = onCapturedLog((record) => {
      if (record.message === 'first work report failed; will retry') firstFailureLogged.resolve()
    })
    const run = host.run(controller.signal)

    try {
      await firstReport.promise
      await firstFailureLogged.promise
      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS)
      await replayedReport.promise

      expect(report).toHaveBeenCalledTimes(2)
      expect(reportedStatuses).toEqual(['unknown', 'unknown'])
      expect(executeWithLog).toHaveBeenCalledTimes(1)

      controller.abort()
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      executeWithLog.mockRestore()
      stopLog()
    }
  })
})
