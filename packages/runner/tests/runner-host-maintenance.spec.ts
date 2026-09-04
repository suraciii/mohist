import { AsyncLocalStorage } from 'node:async_hooks'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { RunnerHost } from '../src/runtime/host.js'
import type { PiRuntime } from '../src/runtime/pi/index.js'
import type { ActionDefinition } from '../src/actions/manifest.js'
import { ActionRegistry } from '../src/actions/registry.js'
import { deferred } from './support/deferred.js'
import type { GitRunner } from '../src/runtime/git-probe.js'
import {
  installFakeOpenCodeRuntimeFactory,
  installReadyOpenCodeRuntimeFactory,
  type OpenCodeRuntimeTestResources,
} from './support/opencode-runtime-factory.js'
import { withTestRunnerResources } from './support/test-resources.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'
import type { RunnerFileSystem } from '../src/system/filesystem.js'
import type { ExternalProcessPolicy } from '../src/system/process-policy.js'
import type { RunnerLogger } from '../src/system/logger.js'
import { createLoggerCapture } from './support/logger-test.js'
import type { OpencodeModelDiscovery } from '../src/runtime/opencode-models.js'

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000

const nonGitRunner: GitRunner = async () => ({
  success: false,
  exitCode: 128,
  stdout: '',
  stderr: 'not a git repository',
  combinedOutput: 'not a git repository',
})

type HostMock = ReturnType<typeof vi.fn>
type HostMocks = Record<
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
  | 'blockingAction',
  HostMock
>

interface HostMockTestState {
  readonly mocks: HostMocks
}

const hostMockStorage = new AsyncLocalStorage<HostMockTestState>()

function currentHostMockTestState(): HostMockTestState {
  const state = hostMockStorage.getStore()
  if (!state) throw new Error('runner host mock resource context is not active')
  return state
}

function scopedMock(name: keyof HostMocks): HostMock {
  const target = (() => undefined) as (...args: unknown[]) => unknown
  Object.defineProperty(target, '_isMockFunction', { value: true })
  return new Proxy(target, {
    apply(_target, thisArg, args) {
      return Reflect.apply(currentHostMockTestState().mocks[name], thisArg, args)
    },
    get(_target, property) {
      const value = Reflect.get(currentHostMockTestState().mocks[name], property)
      return typeof value === 'function' ? value.bind(currentHostMockTestState().mocks[name]) : value
    },
    set(_target, property, value) {
      return Reflect.set(currentHostMockTestState().mocks[name], property, value)
    },
  }) as unknown as HostMock
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
    constructor() {
      void this
    }
  },
}))

vi.mock('../src/actions/registry.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/actions/registry.js')>()
  const definition = (name: string) =>
    ({
      manifest: {
        name,
        inputs: {},
        outputs: [],
        errors: [{ code: 'action-failed', description: 'The test Action failed' }],
      },
      run: blockingAction,
    }) as unknown as ActionDefinition
  return {
    ...actual,
    createDefaultRegistry: () => new actual.ActionRegistry([definition('test/block')]),
  }
})

vi.mock('../src/runtime/workspace.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/runtime/workspace.js')>()
  class FakeWorkspaceManager {
    async prepare() {
      return { path: '/virtual/mohist-runner-host-maintenance', branch: null, changeDir: null }
    }
    async verify() {
      return { path: '/virtual/mohist-runner-host-maintenance', branch: null, changeDir: null }
    }
  }
  return { ...actual, WorkspaceManager: FakeWorkspaceManager }
})

interface HostTestResources extends OpenCodeRuntimeTestResources {
  fileSystem: RunnerFileSystem
  gitRunner: GitRunner
  logger: RunnerLogger
  externalProcessPolicy: ExternalProcessPolicy
  piRuntimeFactory?: () => PiRuntime
  opencodeModelDiscovery?: OpencodeModelDiscovery
}

function it(name: string, body: (resources: HostTestResources) => Promise<void>): void {
  vitestIt(name, async () => {
    const resources: HostTestResources = {
      fileSystem: new MemoryFileSystem(),
      gitRunner: nonGitRunner,
      logger: createLoggerCapture(),
      externalProcessPolicy: {
        assertAllowed(label) {
          throw new Error(`external process forbidden in runner host test: ${label}`)
        },
        register() {},
      },
      piRuntimeFactory: () =>
        ({
          start: async () => ({
            ok: true,
            value: { ready: true, diagnostic: null, catalog: { models: [] } },
            diagnostics: [],
          }),
          ready: () => true,
          diagnostic: () => null,
          catalog: () => ({ models: [] }),
          createSession: async () => ({
            ok: true,
            value: { runtimeSessionId: '/virtual/pi-session', workDir: '/virtual' },
            diagnostics: [],
          }),
          runTurn: async () => ({
            ok: true,
            value: {
              facts: { finalAssistantText: null, runtimeSessionId: '/virtual/pi-session', workDir: '/virtual' },
              diagnostics: [],
            },
            diagnostics: [],
          }),
          shutdown: async () => {},
        }) as never,
    }
    await withTestRunnerResources(async () => {
      await hostMockStorage.run({ mocks: createHostMocks() }, async () => {
        vi.useFakeTimers()
        try {
          installReadyOpenCodeRuntimeFactory(resources)
          await body(resources)
        } finally {
          vi.useRealTimers()
        }
      })
    }, resources)
  })
}

function createHostMocks(): HostMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async () => []),
    report: vi.fn(async () => ({})),
    uploadTaskLog: vi.fn(async () => ({ status: 'changed', accepted: 0, truncated: false })),
    fetchConfig: vi.fn(async () => null),
    startControl: vi.fn(async () => undefined),
    stopControl: vi.fn(async () => undefined),
    getConnectionId: vi.fn(() => 'conn-1'),
    probeLiveness: vi.fn(async () => true),
    blockingAction: vi.fn(),
  }
}

function hostOptions(): ConstructorParameters<typeof RunnerHost>[0] {
  return {
    serverUrl: 'https://runner.test',
    runnerId: 'runner-test',
    projectId: 'project-1',
    runnerRoot: '/virtual/mohist-runner-host-maintenance',
    pollIntervalMs: POLL_INTERVAL_MS,
    heartbeatIntervalMs: QUIET_INTERVAL_MS,
    dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    enabledAgentRuntimes: ['pi', 'opencode'],
  }
}

describe('RunnerHost maintenance lifecycle', () => {
  it('cancels empty-catalog recovery backoff before rediscovery or teardown', async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    const connected = deferred<void>()
    const recoveryStarted = deferred<void>()
    const recoveryRelease = deferred<void>()
    const discovery = vi.fn<OpencodeModelDiscovery>().mockResolvedValue({ models: [], variants: {}, complete: false })
    resources.opencodeModelDiscovery = discovery
    connect.mockImplementation(async () => connected.resolve())
    const waitForConnectionRetry = vi.fn(async (_delayMs: number, signal: AbortSignal) => {
      recoveryStarted.resolve()
      await recoveryRelease.promise
      if (signal.aborted) throw signal.reason
    })
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions(), undefined, { waitForConnectionRetry })
    const run = host.run(controller.signal)
    try {
      await connected.promise
      await vi.advanceTimersByTimeAsync(0)
      await recoveryStarted.promise
      expect(discovery).toHaveBeenCalledOnce()
      expect(waitForConnectionRetry).toHaveBeenCalledWith(5_000, expect.any(AbortSignal))
      const recoverySignal = waitForConnectionRetry.mock.calls[0]?.[1] as AbortSignal

      controller.abort()
      expect(recoverySignal.aborted).toBe(true)
      let stopped = false
      void run.then(() => {
        stopped = true
      })
      await Promise.resolve()
      expect(stopped).toBe(false)

      recoveryRelease.resolve()
      await expect(run).resolves.toBeUndefined()
      expect(discovery).toHaveBeenCalledOnce()
    } finally {
      controller.abort()
      recoveryRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it('waits for blocked heartbeat acknowledgement before runtime and transport teardown', async (resources) => {
    const installed = installFakeOpenCodeRuntimeFactory(resources)
    const connected = deferred<void>()
    const heartbeatStarted = deferred<void>()
    const heartbeatRelease = deferred<void>()
    const disconnectRelease = deferred<void>()
    const controlRelease = deferred<void>()
    const runtimeShutdownStarted = deferred<void>()
    const order: string[] = []
    let heartbeatSignal!: AbortSignal
    connect.mockImplementation(async () => connected.resolve())
    heartbeat.mockImplementation(async (_registration: unknown, signal: AbortSignal) => {
      heartbeatSignal = signal
      heartbeatStarted.resolve()
      await heartbeatRelease.promise
    })
    disconnect.mockImplementation(async () => {
      order.push('transport-disconnect')
      await disconnectRelease.promise
    })
    stopControl.mockImplementation(async () => {
      order.push('control-stop')
      await controlRelease.promise
    })
    const controller = new AbortController()
    const host = new RunnerHost({
      ...hostOptions(),
      pollIntervalMs: QUIET_INTERVAL_MS,
      heartbeatIntervalMs: POLL_INTERVAL_MS,
    })
    const run = host.run(controller.signal)
    try {
      await connected.promise
      const runtime = await installed.runtimeCreated
      vi.spyOn(runtime, 'shutdown').mockImplementation(async () => {
        order.push('runtime-shutdown')
        runtimeShutdownStarted.resolve()
      })
      await vi.advanceTimersByTimeAsync(0)
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      await heartbeatStarted.promise

      controller.abort()
      expect(heartbeatSignal.aborted).toBe(true)
      await Promise.resolve()
      expect(order).toEqual([])

      heartbeatRelease.resolve()
      await runtimeShutdownStarted.promise
      expect(order[0]).toBe('runtime-shutdown')
      expect(order).not.toContain('transport-disconnect')
      expect(order).not.toContain('control-stop')

      disconnectRelease.resolve()
      controlRelease.resolve()
      await expect(run).resolves.toBeUndefined()
    } finally {
      controller.abort()
      heartbeatRelease.resolve()
      disconnectRelease.resolve()
      controlRelease.resolve()
      await run.catch(() => undefined)
    }
  })
})
