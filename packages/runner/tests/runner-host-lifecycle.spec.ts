import { AsyncLocalStorage } from 'node:async_hooks'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { RunnerHost } from '../src/runtime/host.js'
import { getOpenCodeRuntimeFactory } from '../src/runtime/opencode/index.js'
import type { SessionTarget } from '../src/server/session-target.js'
import type { FollowupTargetResolution } from '../src/server/session-target.js'
import { deferred } from './support/deferred.js'
import { capturedLogs, onCapturedLog } from './support/logger-test.js'
import {
  withDefaultRunnerTestResources,
  withTestRunnerResources,
  type DefaultRunnerTestResources,
} from './support/test-resources.js'

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000

type LifecycleMock = ReturnType<typeof vi.fn>
type LifecycleMocks = Record<
  | 'connect'
  | 'heartbeat'
  | 'disconnect'
  | 'poll'
  | 'report'
  | 'uploadTaskLog'
  | 'fetchConfig'
  | 'workflowAgentSessionRuntimeEvents'
  | 'agentSessionRuntimeEvents'
  | 'startControl'
  | 'stopControl'
  | 'disconnectControl'
  | 'getConnectionId'
  | 'probeLiveness'
  | 'blockingAction'
  | 'forceReconnect',
  LifecycleMock
>

interface LifecycleTestState {
  readonly resources: DefaultRunnerTestResources
  readonly mocks: LifecycleMocks
  onReconnected: ((connectionId: string) => void) | null
  followupTargetResolver:
    | ((target: SessionTarget) => FollowupTargetResolution | Promise<FollowupTargetResolution>)
    | null
}

const lifecycleTestStorage = new AsyncLocalStorage<LifecycleTestState>()

function currentLifecycleTestState(): LifecycleTestState {
  const state = lifecycleTestStorage.getStore()
  if (!state) throw new Error('runner host lifecycle test context is not active')
  return state
}

function scopedMock(name: keyof LifecycleMocks): LifecycleMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, '_isMockFunction', { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentLifecycleTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentLifecycleTestState().mocks[name], property)
      return typeof value === 'function' ? value.bind(currentLifecycleTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentLifecycleTestState().mocks[name], property, value)
    },
  }) as unknown as LifecycleMock
}

const connect = scopedMock('connect')
const heartbeat = scopedMock('heartbeat')
const disconnect = scopedMock('disconnect')
const poll = scopedMock('poll')
const report = scopedMock('report')
const uploadTaskLog = scopedMock('uploadTaskLog')
const fetchConfig = scopedMock('fetchConfig')
const workflowAgentSessionRuntimeEvents = scopedMock('workflowAgentSessionRuntimeEvents')
const agentSessionRuntimeEvents = scopedMock('agentSessionRuntimeEvents')
const startControl = scopedMock('startControl')
const stopControl = scopedMock('stopControl')
const disconnectControl = scopedMock('disconnectControl')
const getConnectionId = scopedMock('getConnectionId')
const probeLiveness = scopedMock('probeLiveness')
const blockingAction = scopedMock('blockingAction')
const forceReconnect = scopedMock('forceReconnect')

vi.mock('../src/server/connection.js', () => ({
  ServerConnection: class {
    connect = connect
    heartbeat = heartbeat
    disconnect = disconnect
    poll = poll
    report = report
    uploadTaskLog = uploadTaskLog
    fetchConfig = fetchConfig
    workflowAgentSessionRuntimeEvents = workflowAgentSessionRuntimeEvents
    agentSessionRuntimeEvents = agentSessionRuntimeEvents
  },
}))

vi.mock('../src/server/runner-control-websocket.js', () => ({
  RunnerControlWebSocketClient: class {
    start = startControl
    stop = stopControl
    disconnect = disconnectControl
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
        followupTargetResolver?: (target: SessionTarget) => FollowupTargetResolution | Promise<FollowupTargetResolution>
      } = {},
    ) {
      currentLifecycleTestState().onReconnected = options.onReconnected ?? null
      currentLifecycleTestState().followupTargetResolver = options.followupTargetResolver ?? null
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

function createLifecycleMocks(): LifecycleMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    report: vi.fn(async () => undefined),
    uploadTaskLog: vi.fn(async () => ({ status: 'changed', accepted: 0, truncated: false })),
    fetchConfig: vi.fn(async () => null),
    workflowAgentSessionRuntimeEvents: vi.fn(async () => undefined),
    agentSessionRuntimeEvents: vi.fn(async () => undefined),
    startControl: vi.fn(async () => undefined),
    stopControl: vi.fn(async () => undefined),
    disconnectControl: vi.fn(async () => undefined),
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

function it(name: string, body: (state: LifecycleTestState) => Promise<void> | void): void {
  vitestIt(name, async () => {
    await withDefaultRunnerTestResources(async (resources) => {
      const state: LifecycleTestState = {
        resources,
        mocks: createLifecycleMocks(),
        onReconnected: null,
        followupTargetResolver: null,
      }
      await lifecycleTestStorage.run(state, async () => {
        vi.useFakeTimers()
        try {
          await body(state)
        } finally {
          vi.useRealTimers()
        }
      })
    })
  })
}

describe('RunnerHost', () => {
  it("treats 'opencode' (any casing) as the configured runtime", () => {
    // Issue-410 T-004 retired the SessionCommand handler. The runner
    // host no longer wires a sessionCommandHandler on the control
    // client; the only runtime the runner drives end-to-end is
    // OpenCode, regardless of casing in the wire field.
    expect(getOpenCodeRuntimeFactory()).toBe(currentLifecycleTestState().resources.openCodeRuntimeFactory)
  })

  it('RunnerRegistration_DoesNotReportWorkflowSlots', async () => {
    const connected = deferred<void>()
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue([])
    startControl.mockResolvedValue(undefined)
    stopControl.mockResolvedValue(undefined)
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
      await connected.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(connect).toHaveBeenCalledWith(
        expect.objectContaining({
          projectId: 'project-1',
        }),
        expect.any(AbortSignal),
      )
      const registration = connect.mock.calls[0][0]
      expect(registration).toMatchObject({
        projectId: 'project-1',
        runnerId: 'runner-test',
      })
      expect(registration).toMatchObject({
        runtimeCatalogs: {
          opencode: { models: [], variants: {}, supportsReasoningEffort: false },
          pi: {
            models: [],
            variants: {},
            reasoningEfforts: {},
            supportsReasoningEffort: true,
            complete: true,
            capabilityRevision: expect.any(String),
          },
        },
      })
      for (const identityField of [
        'buildGitHash',
        'component',
        'version',
        'sourceRevision',
        'treeHash',
        'artifactDigest',
        'releaseId',
        'generation',
        'runnerId',
      ]) {
        expect(registration).toHaveProperty(identityField)
      }
      expect(Object.keys(registration).filter((key) => /slot|capacity/i.test(key))).toEqual([])
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('does not claim work when provider policy configuration is invalid', async () => {
    const resources = currentLifecycleTestState().resources
    await withTestRunnerResources(
      async () => {
        const connected = deferred<void>()
        connect.mockImplementation(async () => {
          connected.resolve()
        })
        heartbeat.mockResolvedValue(undefined)
        disconnect.mockResolvedValue(undefined)
        poll.mockResolvedValue([])
        startControl.mockResolvedValue(undefined)
        stopControl.mockResolvedValue(undefined)
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
          await connected.promise
          await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 2)
          expect(poll).not.toHaveBeenCalled()
          expect(capturedLogs()).toEqual(
            expect.arrayContaining([
              expect.objectContaining({ level: 'ERROR', message: 'provider error policy invalid', component: 'host' }),
              expect.objectContaining({
                level: 'WARN',
                message: 'runner not ready; skipping poll',
                fields: expect.objectContaining({ reason: expect.stringContaining('provider error policy invalid') }),
              }),
            ]),
          )
        } finally {
          controller.abort()
          await run.catch(() => undefined)
        }
      },
      { ...resources, environment: { MOHIST_PROVIDER_RETRY_THRESHOLD: '0' } },
    )
  })

  it('WorkerPool_PollsUntilServerReturnsNoWorkWithoutLocalConcurrencyCap', async () => {
    const pollCalls = [deferred<void>(), deferred<void>(), deferred<void>(), deferred<void>()]
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    report.mockResolvedValue(undefined)
    startControl.mockResolvedValue(undefined)
    stopControl.mockResolvedValue(undefined)
    const controller = new AbortController()
    const work = (id: string) => ({
      workflowRunId: '',
      workId: `work-${id}`,
      workType: 'task',
      uses: 'test/block',
      ownerKind: 'agent-job',
      agentJobId: `job-${id}`,
      variables: { workspace: { path: '/virtual/mohist-runner-test' } },
    })
    let pollIndex = 0
    poll.mockImplementation(async () => {
      pollCalls[pollIndex]?.resolve()
      pollIndex += 1
      if (pollIndex === 4) {
        controller.abort()
        return []
      }
      return [work(String(pollIndex))]
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

    const run = host.run(controller.signal)
    try {
      await pollCalls[0]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[1]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[2]!.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await pollCalls[3]!.promise
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('WorkerPool_PollFailure_RetriesWithoutRestartingRunner', async () => {
    const firstPollStarted = deferred<void>()
    const retryPollStarted = deferred<void>()
    const failureLogged = deferred<void>()
    const pollFailure = new Error('server unavailable')
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startControl.mockResolvedValue(undefined)
    stopControl.mockResolvedValue(undefined)
    const controller = new AbortController()
    poll
      .mockImplementationOnce(async () => {
        firstPollStarted.resolve()
        throw pollFailure
      })
      .mockImplementationOnce(async () => {
        retryPollStarted.resolve()
        controller.abort()
        return []
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
    const stopLog = onCapturedLog((record) => {
      if (record.message === 'runner poll failed; retrying') failureLogged.resolve()
    })
    const run = host.run(controller.signal)

    try {
      await firstPollStarted.promise
      await failureLogged.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await retryPollStarted.promise
      await expect(run).resolves.toBeUndefined()

      expect(connect).toHaveBeenCalledTimes(1)
      expect(startControl).toHaveBeenCalledTimes(1)
      expect(startControl).toHaveBeenCalledWith(controller.signal)
      expect(capturedLogs()).toEqual(
        expect.arrayContaining([
          expect.objectContaining({
            level: 'WARN',
            message: 'runner poll failed; retrying',
            fields: expect.objectContaining({ exception: pollFailure }),
          }),
        ]),
      )
    } finally {
      controller.abort()
      await run.catch(() => undefined)
      stopLog()
    }
  })

  it('WorkerPool_PollTimeout_AbortsAttemptAndContinuesPolling', async () => {
    const firstPollStarted = deferred<void>()
    const retryPollStarted = deferred<void>()
    const pollAbort = deferred<void>()
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    startControl.mockResolvedValue(undefined)
    stopControl.mockResolvedValue(undefined)
    const controller = new AbortController()
    poll
      .mockImplementationOnce(
        (signal: AbortSignal) =>
          new Promise((_, reject) => {
            firstPollStarted.resolve()
            signal.addEventListener(
              'abort',
              () => {
                pollAbort.resolve()
                reject(signal.reason)
              },
              { once: true },
            )
          }),
      )
      .mockImplementationOnce(async () => {
        retryPollStarted.resolve()
        controller.abort()
        return []
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
    const run = host.run(controller.signal)

    try {
      await firstPollStarted.promise
      await vi.advanceTimersByTimeAsync(10_000 + POLL_INTERVAL_MS + 1)
      await pollAbort.promise
      await retryPollStarted.promise
      await expect(run).resolves.toBeUndefined()

      expect(capturedLogs()).toEqual(
        expect.arrayContaining([
          expect.objectContaining({
            level: 'WARN',
            message: 'runner poll failed; retrying',
            fields: expect.objectContaining({ exception: expect.any(Error) }),
          }),
        ]),
      )
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('RunnerShutdown_UnregistersRunner', async () => {
    const connected = deferred<void>()
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockResolvedValue(undefined)
    poll.mockResolvedValue([])
    startControl.mockResolvedValue(undefined)
    stopControl.mockResolvedValue(undefined)
    const controller = new AbortController()
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: POLL_INTERVAL_MS,
      heartbeatIntervalMs: QUIET_INTERVAL_MS,
      dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    })

    const run = host.run(controller.signal)
    try {
      await connected.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))
      expect(stopControl).toHaveBeenCalled()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('RunnerConnection_WhenControlFails_DoesNotPollAndRetriesCleanly', async () => {
    const firstControlStarted = deferred<void>()
    const secondControlStarted = deferred<void>()
    const secondControlRelease = deferred<void>()
    const disconnectedAfterFailure = deferred<void>()
    const retryWaitStarted = deferred<number>()
    const retryWaitRelease = deferred<void>()
    const firstPollStarted = deferred<void>()
    getConnectionId.mockReturnValue('conn-1')
    probeLiveness.mockResolvedValue(true)
    forceReconnect.mockResolvedValue(undefined)
    connect.mockResolvedValue(undefined)
    heartbeat.mockResolvedValue(undefined)
    disconnect.mockImplementation(async () => {
      disconnectedAfterFailure.resolve()
    })
    poll.mockImplementation(async () => {
      firstPollStarted.resolve()
      return []
    })
    const controlUnavailable = new Error('control unavailable')
    startControl
      .mockImplementationOnce(async () => {
        firstControlStarted.resolve()
        throw controlUnavailable
      })
      .mockImplementationOnce(async () => {
        secondControlStarted.resolve()
        await secondControlRelease.promise
      })
    stopControl.mockResolvedValue(undefined)
    const waitForConnectionRetry = vi.fn(async (delayMs: number, signal: AbortSignal) => {
      retryWaitStarted.resolve(delayMs)
      await retryWaitRelease.promise
      if (signal.aborted) throw signal.reason
    })
    const controller = new AbortController()
    const host = new RunnerHost(
      {
        serverUrl: 'https://runner.test',
        runnerId: 'runner-test',
        runnerRoot: '/virtual/mohist-runner-test',
        pollIntervalMs: POLL_INTERVAL_MS,
        heartbeatIntervalMs: QUIET_INTERVAL_MS,
        dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
      },
      undefined,
      { waitForConnectionRetry },
    )

    const run = host.run(controller.signal)
    try {
      await firstControlStarted.promise
      await disconnectedAfterFailure.promise
      await expect(retryWaitStarted.promise).resolves.toBe(POLL_INTERVAL_MS)
      expect(waitForConnectionRetry).toHaveBeenCalledWith(POLL_INTERVAL_MS, controller.signal)
      retryWaitRelease.resolve()
      await secondControlStarted.promise
      expect(poll).not.toHaveBeenCalled()
      expect(disconnect).toHaveBeenCalledWith(expect.any(AbortSignal))
      expect(disconnectControl).toHaveBeenCalledTimes(1)
      expect(stopControl).not.toHaveBeenCalled()

      secondControlRelease.resolve()
      await firstPollStarted.promise
      controller.abort()
      await expect(run).resolves.toBeUndefined()

      expect(capturedLogs()).toEqual(
        expect.arrayContaining([
          expect.objectContaining({
            level: 'ERROR',
            message: 'runner connection failed; retrying',
            fields: expect.objectContaining({ exception: controlUnavailable }),
          }),
        ]),
      )
    } finally {
      retryWaitRelease.resolve()
      secondControlRelease.resolve()
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('ClaimReadiness_RequiresHealthyRuntimeEventOutbox', () => {
    const host = new RunnerHost({
      serverUrl: 'https://runner.test',
      runnerId: 'runner-test',
      runnerRoot: '/virtual/mohist-runner-test',
      pollIntervalMs: 1,
      heartbeatIntervalMs: 60_000,
      dispatchLivenessProbeIntervalMs: 60_000,
    }) as unknown as {
      openCodeRuntime: { ready(): boolean } | null
      agentSessionRuntimeEventOutbox: { ready(): boolean }
      isOpenCodeReadyForClaim(): boolean
    }
    host.openCodeRuntime = { ready: () => true }
    host.agentSessionRuntimeEventOutbox = { ready: () => false }

    expect(host.isOpenCodeReadyForClaim()).toBe(false)

    host.agentSessionRuntimeEventOutbox = { ready: () => true }
    expect(host.isOpenCodeReadyForClaim()).toBe(true)
  })
})
