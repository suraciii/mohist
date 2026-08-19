import { AsyncLocalStorage } from 'node:async_hooks'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { RunnerHost } from '../src/runtime/host.js'
import { WorkResultJournal } from '../src/runtime/work-result-journal.js'
import { WorkExecutor } from '../src/runtime/executor.js'
import type { SessionTarget } from '../src/server/session-target.js'
import { deferred } from './support/deferred.js'
import { onCapturedLog } from './support/logger-test.js'
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
  | 'startControl'
  | 'stopControl'
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
const startControl = scopedMock('startControl')
const stopControl = scopedMock('stopControl')
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
    startControl: vi.fn(async () => undefined),
    stopControl: vi.fn(async () => undefined),
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

vi.mock('../src/server/runner-control-websocket.js', () => ({
  RunnerControlWebSocketClient: class {
    start = startControl
    stop = stopControl
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
    startControl.mockResolvedValue(undefined)
    stopControl.mockResolvedValue(undefined)
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

  it('RecoveryDispatch_RearmsTheStartedFenceAndReconcilesUnderTheOriginalIdentity', async () => {
    const receiptAcknowledged = deferred<void>()
    const fencedWork = {
      workflowRunId: 'wr-recovery-rearm',
      workId: 'work-recovery-rearm',
      taskRunId: 'task-recovery-rearm',
      workType: 'task',
      uses: 'mohist/pi',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    }
    const recoveryDispatch = {
      ...fencedWork,
      variables: { workspace: { path: '/virtual/mohist-runner-test-2' } },
      agentRecovery: { runtime: 'pi', runtimeSessionId: '/virtual/mohist-runner-test/sessions/pi-1' },
    }
    const journal = new WorkResultJournal('/virtual/mohist-runner-test')
    await journal.load()
    await journal.begin(fencedWork)

    let attempts = 0
    report.mockImplementation(async () => {
      attempts += 1
      if (attempts === 1) return { tracked: false, reason: 'observation-not-durable' }
      return { tracked: true, reason: 'accepted' }
    })
    poll.mockResolvedValueOnce([recoveryDispatch]).mockResolvedValue([])
    let executions = 0
    let executedWork: unknown = null
    const executeWithLog = vi
      .spyOn(WorkExecutor.prototype, 'executeWithLog')
      .mockImplementation(async (work, _signal, collector) => {
        executions += 1
        executedWork = work
        return {
          result: { status: 'completed', output: null },
          collector: collector!,
        }
      })
    const originalAcknowledge = WorkResultJournal.prototype.acknowledge
    const acknowledge = vi.spyOn(WorkResultJournal.prototype, 'acknowledge').mockImplementation(async function (
      this: WorkResultJournal,
      acknowledged,
    ) {
      await originalAcknowledge.call(this, acknowledged)
      if (acknowledged.workId === fencedWork.workId) receiptAcknowledged.resolve()
    })
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
      await receiptAcknowledged.promise

      expect(executions).toBe(1)
      expect(executedWork).toEqual(recoveryDispatch)
      expect(report.mock.calls.map((call) => call[1]?.status)).toEqual(['unknown', 'completed'])
      expect(report).toHaveBeenLastCalledWith(
        expect.objectContaining({ workId: fencedWork.workId, taskRunId: fencedWork.taskRunId }),
        expect.objectContaining({ status: 'completed' }),
        expect.any(AbortSignal),
      )

      // The startup unknown observation must not fire again after the
      // delivery-driven reconciliation took over its identity.
      await vi.advanceTimersByTimeAsync(AWAITING_ACK_RETRY_INTERVAL_MS * 2)
      expect(report.mock.calls.map((call) => call[1]?.status)).toEqual(['unknown', 'completed'])

      const retired = new WorkResultJournal('/virtual/mohist-runner-test')
      await retired.load()
      expect(retired.started()).toEqual([])
      expect(retired.completed()).toEqual([])
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      acknowledge.mockRestore()
      executeWithLog.mockRestore()
    }
  })

  it('RecoveryDispatch_WithoutATurnAdoptionRuntime_ReportsUnknownWithoutExecuting', async () => {
    const observationAcknowledged = deferred<void>()
    const work = {
      workflowRunId: 'wr-recovery-opencode',
      workId: 'work-recovery-opencode',
      taskRunId: 'task-recovery-opencode',
      workType: 'task',
      uses: 'mohist/opencode',
      ownerKind: 'workflow',
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
      agentRecovery: { runtime: 'opencode', runtimeSessionId: 'opencode-session-1' },
    }
    const journal = new WorkResultJournal('/virtual/mohist-runner-test')
    await journal.load()

    report.mockResolvedValue({ tracked: true, reason: 'accepted' })
    poll.mockResolvedValueOnce([work]).mockResolvedValue([])
    const executeWithLog = vi.spyOn(WorkExecutor.prototype, 'executeWithLog').mockImplementation(async () => {
      throw new Error('recovery dispatch must not execute')
    })
    const originalAcknowledgeUnconfirmed = WorkResultJournal.prototype.acknowledgeUnconfirmed
    const acknowledgeUnconfirmed = vi
      .spyOn(WorkResultJournal.prototype, 'acknowledgeUnconfirmed')
      .mockImplementation(async function (this: WorkResultJournal, acknowledged) {
        await originalAcknowledgeUnconfirmed.call(this, acknowledged)
        if (acknowledged.workId === work.workId) observationAcknowledged.resolve()
      })
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
      await vi.advanceTimersByTimeAsync(5)
      await observationAcknowledged.promise

      expect(report).toHaveBeenCalledTimes(1)
      expect(report).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowRunId: work.workflowRunId,
          workId: work.workId,
          taskRunId: work.taskRunId,
        }),
        expect.objectContaining({ status: 'unknown' }),
        expect.any(AbortSignal),
      )

      const retired = new WorkResultJournal('/virtual/mohist-runner-test')
      await retired.load()
      expect(retired.started()).toEqual([])
      expect(retired.completed()).toEqual([])
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      acknowledgeUnconfirmed.mockRestore()
      executeWithLog.mockRestore()
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
})
