import { AsyncLocalStorage } from 'node:async_hooks'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { RunnerHost } from '../src/runtime/host.js'
import { WorkResultJournal } from '../src/runtime/work-result-journal.js'
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
  it('Restart_RedrivesDurablyCompletedResultWithoutExecutingTheWorkAgain', async () => {
    const redriven = deferred<void>()
    const work = {
      workflowRunId: 'wr-restart',
      workId: 'work-restart',
      taskRunId: 'task-restart',
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    const journal = new WorkResultJournal('/virtual/mohist-runner-test')
    await journal.load()
    await journal.begin(work)
    await journal.complete(work, { status: 'failed', message: 'result persisted before process exit' })

    const controller = new AbortController()
    report.mockImplementation(async (reportedWork: { workId?: string }) => {
      if (reportedWork.workId === work.workId) redriven.resolve()
      return { tracked: true, reason: 'accepted' }
    })
    poll.mockResolvedValue([])
    startSignalR.mockResolvedValue(undefined)
    stopSignalR.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await redriven.promise
      expect(report).toHaveBeenCalledWith(
        expect.objectContaining({ workId: work.workId }),
        expect.objectContaining({ status: 'failed', message: 'result persisted before process exit' }),
        expect.any(AbortSignal),
      )
      expect(blockingAction).not.toHaveBeenCalled()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('Restart_ReportsOnlyRecoveredAgentStartedFencesAsUnknownWithoutReplayingWork', async () => {
    const observationAcknowledged = deferred<void>()
    const agentWork = {
      workflowRunId: 'wr-recovered-agent-started',
      workId: 'work-recovered-agent-started',
      taskRunId: 'task-recovered-agent-started',
      workType: 'task',
      uses: 'mohist/pi',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    const ordinaryWork = {
      workflowRunId: 'wr-recovered-ordinary-started',
      workId: 'work-recovered-ordinary-started',
      taskRunId: 'task-recovered-ordinary-started',
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    const journal = new WorkResultJournal('/virtual/mohist-runner-test')
    await journal.load()
    await journal.begin(agentWork)
    await journal.begin(ordinaryWork)

    const originalAcknowledgeUnconfirmed = WorkResultJournal.prototype.acknowledgeUnconfirmed
    const acknowledgeUnconfirmed = vi
      .spyOn(WorkResultJournal.prototype, 'acknowledgeUnconfirmed')
      .mockImplementation(async function (this: WorkResultJournal, work) {
        await originalAcknowledgeUnconfirmed.call(this, work)
        if (work.workId === agentWork.workId) observationAcknowledged.resolve()
      })
    report.mockResolvedValue({ tracked: true, reason: 'accepted' })
    poll.mockResolvedValue([])
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const run = host.run(controller.signal)

    try {
      await observationAcknowledged.promise

      expect(report).toHaveBeenCalledTimes(1)
      expect(report).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowRunId: agentWork.workflowRunId,
          workId: agentWork.workId,
          taskRunId: agentWork.taskRunId,
        }),
        expect.objectContaining({
          status: 'unknown',
          message: 'Runner restarted after a durable started fence without a completed result receipt.',
        }),
        expect.any(AbortSignal),
      )
      expect(blockingAction).not.toHaveBeenCalled()

      const persisted = new WorkResultJournal('/virtual/mohist-runner-test')
      await persisted.load()
      expect(persisted.started()).toEqual([{ work: ordinaryWork, state: 'started' }])
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      acknowledgeUnconfirmed.mockRestore()
    }
  })

  it('Restart_ReportsRecoveredAgentJobStartedFenceAsUnknownWithoutReplayingWork', async () => {
    const observationAcknowledged = deferred<void>()
    const work = {
      workflowRunId: '',
      workId: 'work-recovered-agent-job-started',
      workType: 'agent-job',
      ownerKind: 'agent-job',
      agentJobId: 'agent-job-recovered-started',
      agentSessionId: 'session-recovered-started',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    const journal = new WorkResultJournal('/virtual/mohist-runner-test')
    await journal.load()
    await journal.begin(work)

    const originalAcknowledgeUnconfirmed = WorkResultJournal.prototype.acknowledgeUnconfirmed
    const acknowledgeUnconfirmed = vi
      .spyOn(WorkResultJournal.prototype, 'acknowledgeUnconfirmed')
      .mockImplementation(async function (this: WorkResultJournal, acknowledged) {
        await originalAcknowledgeUnconfirmed.call(this, acknowledged)
        if (acknowledged.workId === work.workId) observationAcknowledged.resolve()
      })
    report.mockResolvedValue({ tracked: true, reason: 'unknown' })
    poll.mockResolvedValue([])
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const run = host.run(controller.signal)

    try {
      await observationAcknowledged.promise

      expect(report).toHaveBeenCalledTimes(1)
      expect(report).toHaveBeenCalledWith(
        expect.objectContaining({
          ownerKind: 'agent-job',
          agentJobId: work.agentJobId,
          workId: work.workId,
        }),
        expect.objectContaining({
          status: 'unknown',
          message: 'Runner restarted after a durable started fence without a completed result receipt.',
        }),
        expect.any(AbortSignal),
      )
      expect(blockingAction).not.toHaveBeenCalled()

      const persisted = new WorkResultJournal('/virtual/mohist-runner-test')
      await persisted.load()
      expect(persisted.started()).toEqual([])
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      acknowledgeUnconfirmed.mockRestore()
    }
  })

  it('Restart_RetriesRecoveredAgentStartedObservationUntilItsFenceCanBeDurablyRetired', async () => {
    const firstObservation = deferred<void>()
    const retired = deferred<void>()
    const work = {
      workflowRunId: 'wr-recovered-started-retry',
      workId: 'work-recovered-started-retry',
      taskRunId: 'task-recovered-started-retry',
      workType: 'task',
      uses: 'mohist/opencode',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    const journal = new WorkResultJournal('/virtual/mohist-runner-test')
    await journal.load()
    await journal.begin(work)

    let attempts = 0
    report.mockImplementation(async () => {
      attempts += 1
      if (attempts === 1) {
        firstObservation.resolve()
        return { tracked: false, reason: 'observation-not-durable' }
      }
      return { tracked: true, reason: 'accepted' }
    })
    const originalAcknowledgeUnconfirmed = WorkResultJournal.prototype.acknowledgeUnconfirmed
    const acknowledgeUnconfirmed = vi
      .spyOn(WorkResultJournal.prototype, 'acknowledgeUnconfirmed')
      .mockImplementation(async function (this: WorkResultJournal, acknowledged) {
        await originalAcknowledgeUnconfirmed.call(this, acknowledged)
        if (acknowledged.workId === work.workId) retired.resolve()
      })
    poll.mockResolvedValue([])
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const run = host.run(controller.signal)

    try {
      await firstObservation.promise
      const retained = new WorkResultJournal('/virtual/mohist-runner-test')
      await retained.load()
      expect(retained.started()).toEqual([{ work, state: 'started' }])

      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS)
      await retired.promise

      expect(report.mock.calls.map((calls) => calls[1]?.status)).toEqual(['unknown', 'unknown'])
      const retiredJournal = new WorkResultJournal('/virtual/mohist-runner-test')
      await retiredJournal.load()
      expect(retiredJournal.started()).toEqual([])
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      acknowledgeUnconfirmed.mockRestore()
    }
  })

  it('AbortAfterReturnedResult_PersistsAndRedrivesTheReceiptWithoutReexecution', async () => {
    const executionStarted = deferred<void>()
    const receiptAcknowledged = deferred<void>()
    const work = {
      workflowRunId: 'wr-abort-receipt',
      workId: 'work-abort-receipt',
      taskRunId: 'task-abort-receipt',
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    let executions = 0
    const executeWithLog = vi
      .spyOn(WorkExecutor.prototype, 'executeWithLog')
      .mockImplementation(async (_work, signal, collector) => {
        executions += 1
        executionStarted.resolve()
        await new Promise<void>((resolve) => signal.addEventListener('abort', () => resolve(), { once: true }))
        return {
          result: {
            status: 'failed',
            message: 'runtime returned terminal failure after cancellation',
            error: { code: 'action-failed', message: 'runtime returned terminal failure after cancellation' },
            exitCode: 1,
          },
          collector: collector!,
        }
      })
    poll.mockResolvedValueOnce([work]).mockResolvedValue([])
    report.mockRejectedValueOnce(new Error('first report transport failed'))

    const firstController = new AbortController()
    const firstHost = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      projectId: 'project-1',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })
    const firstRun = firstHost.run(firstController.signal)
    const originalAcknowledge = WorkResultJournal.prototype.acknowledge
    const acknowledge = vi.spyOn(WorkResultJournal.prototype, 'acknowledge').mockImplementation(async function (
      this: WorkResultJournal,
      acknowledged,
    ) {
      await originalAcknowledge.call(this, acknowledged)
      if (acknowledged.workId === work.workId) receiptAcknowledged.resolve()
    })
    let restarted: { controller: AbortController; run: Promise<void> } | null = null

    try {
      await executionStarted.promise
      firstController.abort()
      await expect(firstRun).resolves.toBeUndefined()

      const persisted = new WorkResultJournal('/virtual/mohist-runner-test')
      await persisted.load()
      expect(persisted.completed()).toEqual([
        expect.objectContaining({
          work: expect.objectContaining({ workflowRunId: work.workflowRunId, workId: work.workId }),
          state: 'completed',
          result: expect.objectContaining({
            status: 'failed',
            message: 'runtime returned terminal failure after cancellation',
          }),
        }),
      ])

      report.mockResolvedValue({ tracked: true })
      poll.mockResolvedValue([])
      const controller = new AbortController()
      const host = new RunnerHost({
        serverUrl: 'https://runner.test',
        runnerId: 'runner-test',
        projectId: 'project-1',
        runnerRoot: '/virtual/mohist-runner-test',
        pollIntervalMs: QUIET_INTERVAL_MS,
        heartbeatIntervalMs: QUIET_INTERVAL_MS,
        dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
      })
      restarted = { controller, run: host.run(controller.signal) }

      await receiptAcknowledged.promise
      expect(report).toHaveBeenCalledWith(
        expect.objectContaining({ workId: work.workId }),
        expect.objectContaining({ status: 'failed', message: 'runtime returned terminal failure after cancellation' }),
        expect.any(AbortSignal),
      )
      expect(executions).toBe(1)
      const acknowledged = new WorkResultJournal('/virtual/mohist-runner-test')
      await acknowledged.load()
      expect(acknowledged.completed()).toEqual([])
    } finally {
      firstController.abort()
      await firstRun.catch(() => undefined)
      restarted?.controller.abort()
      await restarted?.run.catch(() => undefined)
      acknowledge.mockRestore()
      executeWithLog.mockRestore()
    }
  })

  it('PendingJournalCompletion_IsNotReportedUntilTheNextControlPlaneBoundaryMakesItDurable', async () => {
    const completionHeld = deferred<void>()
    const pendingRedelivery = deferred<void>()
    const recoveredReport = deferred<void>()
    const work = {
      workflowRunId: 'wr-pending-journal',
      workId: 'work-pending-journal',
      taskRunId: 'task-pending-journal',
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    let executions = 0
    let failCompletedJournalWrite = true
    const fileSystem = currentReportingTestState().resources.fileSystem
    const originalWriteText = fileSystem.writeText.bind(fileSystem)
    const writeText = vi.spyOn(fileSystem, 'writeText').mockImplementation(async (path, content, options) => {
      if (
        failCompletedJournalWrite &&
        path.endsWith('/.mohist/runner-state/work-results.json.tmp') &&
        content.includes('"state": "completed"')
      ) {
        throw new Error('ENOSPC')
      }
      await originalWriteText(path, content, options)
    })
    const executeWithLog = vi
      .spyOn(WorkExecutor.prototype, 'executeWithLog')
      .mockImplementation(async (_work, _signal, collector) => {
        executions += 1
        return {
          result: { status: 'completed', output: { answer: 'retained until durable' } },
          collector: collector!,
        }
      })
    const stopLog = onCapturedLog((record) => {
      if (record.message === 'work result journal persistence deferred; retaining result in memory')
        completionHeld.resolve()
    })
    let pollCount = 0
    poll.mockImplementation(async () => {
      pollCount += 1
      if (pollCount === 1) return [work]
      if (pollCount === 2) {
        pendingRedelivery.resolve()
        return [work]
      }
      return []
    })
    report.mockImplementation(async (reportedWork: { workId?: string }) => {
      if (reportedWork.workId === work.workId) recoveredReport.resolve()
      return { tracked: true, reason: 'accepted' }
    })
    const controller = new AbortController()
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
      await completionHeld.promise
      expect(report).not.toHaveBeenCalled()
      expect(executions).toBe(1)

      // A new process can see only the persisted started fence while the
      // original host retains its exact result in memory.
      const restarted = new WorkResultJournal('/virtual/mohist-runner-test')
      await restarted.load()
      expect(await restarted.begin(work)).toBe('started')

      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pendingRedelivery.promise
      expect(report).not.toHaveBeenCalled()
      expect(executions).toBe(1)

      failCompletedJournalWrite = false
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await recoveredReport.promise

      expect(report).toHaveBeenCalledTimes(1)
      expect(report).toHaveBeenCalledWith(
        expect.objectContaining({ workId: work.workId }),
        expect.objectContaining({ status: 'completed', output: { answer: 'retained until durable' } }),
        expect.any(AbortSignal),
      )
      expect(executions).toBe(1)
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      writeText.mockRestore()
      executeWithLog.mockRestore()
      stopLog()
    }
  })

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
