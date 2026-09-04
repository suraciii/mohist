import { AsyncLocalStorage } from 'node:async_hooks'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { RunnerHost } from '../src/runtime/host.js'
import type { PiRuntime } from '../src/runtime/pi/index.js'
import type { ActionDefinition } from '../src/actions/manifest.js'
import { ActionRegistry } from '../src/actions/registry.js'
import { deferred } from './support/deferred.js'
import type { GitRunner } from '../src/runtime/git-probe.js'
import { UnexpectedConsoleRecorder } from './support/unexpected-console.js'
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
import { MANAGER_OPENCODE_CAPABILITY, MANAGER_PI_CAPABILITIES } from '../src/runtime/host-helpers.js'

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
  | 'blockingAction'
  | 'forceReconnect',
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
  },
}))

vi.mock('../src/server/runner-control-websocket.js', () => ({
  RunnerControlWebSocketClient: class {
    start = startControl
    stop = stopControl
    getConnectionId = getConnectionId
    probeLiveness = probeLiveness
    forceReconnect = forceReconnect
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
        description: name === 'test/catalog' ? 'Catalog test Action' : undefined,
        inputs:
          name === 'test/catalog'
            ? {
                prompt: { types: ['string', 'object'] as const, required: true as const, description: 'Prompt value' },
                timeout: { types: ['number'] as const, default: 30, description: 'Timeout in milliseconds' },
              }
            : {},
        outputs: name === 'test/catalog' ? [{ name: 'public', description: 'Public result' }] : [],
        errors: [{ code: 'action-failed', description: 'The test Action failed' }],
      },
      run: blockingAction,
    }) as unknown as ActionDefinition
  return {
    ...actual,
    createDefaultRegistry: () =>
      new actual.ActionRegistry([definition('test/block'), definition('test/observe'), definition('test/catalog')]),
  }
})

vi.mock('../src/runtime/workspace.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/runtime/workspace.js')>()
  class FakeWorkspaceManager {
    async prepare() {
      return { path: '/virtual/mohist-runner-host-opencode-runtime', branch: null, changeDir: null }
    }
    async verify() {
      return { path: '/virtual/mohist-runner-host-opencode-runtime', branch: null, changeDir: null }
    }
  }
  return {
    ...actual,
    WorkspaceManager: FakeWorkspaceManager,
  }
})

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
    forceReconnect: vi.fn(async () => undefined),
  }
}

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

function baseHostOptions(): ConstructorParameters<typeof RunnerHost>[0] {
  return {
    serverUrl: 'https://runner.test',
    runnerId: 'runner-test',
    projectId: 'project-1',
    runnerRoot: '/virtual/mohist-runner-host-opencode-runtime',
    pollIntervalMs: POLL_INTERVAL_MS,
    heartbeatIntervalMs: QUIET_INTERVAL_MS,
    dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
  }
}

function hostOptions(): ConstructorParameters<typeof RunnerHost>[0] {
  return {
    ...baseHostOptions(),
    enabledAgentRuntimes: ['pi', 'opencode'],
  }
}

function hostWithFakeTerminalDelivery(): RunnerHost {
  return new RunnerHost(hostOptions())
}

function workflowVariables(): Record<string, unknown> {
  return {
    repository: { gitUrl: 'https://example.com/repo.git', baseBranch: 'main' },
    issue: { number: 1 },
    workspace: { path: '/virtual/mohist-runner-host-opencode-runtime' },
    mohist: { runId: 'wr-test' },
  }
}

function expectedActionCatalog() {
  const error = { code: 'action-failed', description: 'The test Action failed' }
  return {
    actions: [
      { name: 'test/block', inputs: [], outputs: [], errors: [error] },
      {
        name: 'test/catalog',
        description: 'Catalog test Action',
        inputs: [
          { name: 'prompt', types: ['string', 'object'], required: true, description: 'Prompt value' },
          { name: 'timeout', types: ['number'], required: false, default: 30, description: 'Timeout in milliseconds' },
        ],
        outputs: [{ name: 'public', description: 'Public result' }],
        errors: [error],
      },
      { name: 'test/observe', inputs: [], outputs: [], errors: [error] },
    ],
    tombstones: [],
  }
}

function runtimeActionRegistry(): ActionRegistry {
  const definition = (name: string): ActionDefinition => ({
    manifest: {
      name,
      inputs: {},
      outputs: [],
      errors: [{ code: 'action-failed', description: 'The test Action failed' }],
    },
    run: async () => ({ output: {} }),
  })
  return new ActionRegistry([definition('mohist/opencode'), definition('mohist/pi'), definition('test/shared')])
}

describe('RunnerHost wires the OpenCodeRuntime lifecycle', () => {
  it('defaults to Pi without constructing OpenCode and advertises only Pi runtime surfaces', async (resources) => {
    const openCodeFactory = vi.fn(resources.openCodeRuntimeFactory!)
    resources.openCodeRuntimeFactory = openCodeFactory
    const originalPiFactory = resources.piRuntimeFactory!
    const piRuntime = originalPiFactory()
    const piStart = vi.spyOn(piRuntime, 'start')
    const piShutdown = vi.spyOn(piRuntime, 'shutdown')
    const piFactory = vi.fn(() => piRuntime)
    resources.piRuntimeFactory = piFactory
    const connected = deferred<void>()
    const polled = deferred<void>()
    connect.mockImplementation(async () => connected.resolve())
    poll.mockImplementation(async () => {
      polled.resolve()
      return []
    })
    const controller = new AbortController()
    const host = new RunnerHost(baseHostOptions(), runtimeActionRegistry())
    const run = host.run(controller.signal)
    try {
      await connected.promise
      await polled.promise

      expect(openCodeFactory).not.toHaveBeenCalled()
      expect(piFactory).toHaveBeenCalledTimes(1)
      expect(piStart).toHaveBeenCalledTimes(1)
      const registration = connect.mock.calls[0]?.[0]
      expect(Object.keys(registration.runtimeCatalogs)).toEqual(['pi'])
      expect(registration.capabilities).toEqual(expect.arrayContaining([...MANAGER_PI_CAPABILITIES]))
      expect(registration.capabilities).not.toContain(MANAGER_OPENCODE_CAPABILITY)
      expect(registration.actionCatalog.actions.map((action: { name: string }) => action.name)).toEqual([
        'mohist/pi',
        'test/shared',
      ])
      expect(poll.mock.calls[0]?.[1]).toMatchObject({
        runtimeReadiness: [{ runtime: 'pi', ready: true, generation: 1 }],
      })
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
    expect(piShutdown).toHaveBeenCalledTimes(1)
  })

  it('keeps Pi and OpenCode lifecycle surfaces when both runtimes are explicitly enabled', async (resources) => {
    const installed = installFakeOpenCodeRuntimeFactory(resources)
    const originalOpenCodeFactory = resources.openCodeRuntimeFactory!
    let openCodeShutdown: ReturnType<typeof vi.spyOn> | null = null
    const openCodeFactory = vi.fn((deps) => {
      const runtime = originalOpenCodeFactory(deps)
      openCodeShutdown = vi.spyOn(runtime, 'shutdown')
      return runtime
    })
    resources.openCodeRuntimeFactory = openCodeFactory
    const originalPiFactory = resources.piRuntimeFactory!
    const piRuntime = originalPiFactory()
    const piStart = vi.spyOn(piRuntime, 'start')
    const piShutdown = vi.spyOn(piRuntime, 'shutdown')
    const piFactory = vi.fn(() => piRuntime)
    resources.piRuntimeFactory = piFactory
    const connected = deferred<void>()
    const polled = deferred<void>()
    connect.mockImplementation(async () => connected.resolve())
    poll.mockImplementation(async () => {
      polled.resolve()
      return []
    })
    const controller = new AbortController()
    const host = new RunnerHost(hostOptions(), runtimeActionRegistry())
    const run = host.run(controller.signal)
    try {
      await connected.promise
      await polled.promise

      expect(openCodeFactory).toHaveBeenCalledTimes(1)
      expect(installed.client.health).toHaveBeenCalledTimes(1)
      expect(piFactory).toHaveBeenCalledTimes(1)
      expect(piStart).toHaveBeenCalledTimes(1)
      const registration = connect.mock.calls[0]?.[0]
      expect(Object.keys(registration.runtimeCatalogs).sort()).toEqual(['opencode', 'pi'])
      expect(registration.capabilities).toEqual(
        expect.arrayContaining([...MANAGER_PI_CAPABILITIES, MANAGER_OPENCODE_CAPABILITY]),
      )
      expect(registration.actionCatalog.actions.map((action: { name: string }) => action.name)).toEqual([
        'mohist/opencode',
        'mohist/pi',
        'test/shared',
      ])
      expect(poll.mock.calls[0]?.[1]).toMatchObject({
        runtimeReadiness: [
          { runtime: 'opencode', ready: true, generation: expect.any(Number) },
          { runtime: 'pi', ready: true, generation: 1 },
        ],
      })
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
    expect(openCodeShutdown).not.toBeNull()
    expect(openCodeShutdown!).toHaveBeenCalledTimes(1)
    expect(piShutdown).toHaveBeenCalledTimes(1)
  })

  it('shuts down OpenCode once when the enabled Pi factory throws during startup', async (resources) => {
    const installed = installFakeOpenCodeRuntimeFactory(resources)
    const originalOpenCodeFactory = resources.openCodeRuntimeFactory!
    let openCodeShutdown: ReturnType<typeof vi.spyOn> | null = null
    resources.openCodeRuntimeFactory = (deps) => {
      const runtime = originalOpenCodeFactory(deps)
      openCodeShutdown = vi.spyOn(runtime, 'shutdown')
      return runtime
    }
    resources.piRuntimeFactory = () => {
      throw new Error('Pi factory failed')
    }
    const host = new RunnerHost(hostOptions())

    await expect(host.run(new AbortController().signal)).rejects.toThrow('Pi factory failed')

    expect(installed.client.health).toHaveBeenCalledTimes(1)
    expect(openCodeShutdown).not.toBeNull()
    expect(openCodeShutdown!).toHaveBeenCalledTimes(1)
  })

  it('shuts down every created runtime once when enabled Pi start throws', async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    const originalOpenCodeFactory = resources.openCodeRuntimeFactory!
    let openCodeShutdown: ReturnType<typeof vi.spyOn> | null = null
    resources.openCodeRuntimeFactory = (deps) => {
      const runtime = originalOpenCodeFactory(deps)
      openCodeShutdown = vi.spyOn(runtime, 'shutdown')
      return runtime
    }
    const piShutdown = vi.fn(async () => undefined)
    resources.piRuntimeFactory = () =>
      ({
        start: vi.fn(async () => {
          throw new Error('Pi start failed')
        }),
        ready: () => false,
        diagnostic: () => null,
        catalog: () => null,
        shutdown: piShutdown,
      }) as never
    const host = new RunnerHost(hostOptions())

    await expect(host.run(new AbortController().signal)).rejects.toThrow('Pi start failed')

    expect(openCodeShutdown).not.toBeNull()
    expect(openCodeShutdown!).toHaveBeenCalledTimes(1)
    expect(piShutdown).toHaveBeenCalledTimes(1)
  })

  it('retries an empty startup model catalog and publishes the recovered catalog', async (resources) => {
    const discovery = vi
      .fn<OpencodeModelDiscovery>()
      .mockResolvedValueOnce({ models: [], variants: {}, complete: false })
      .mockResolvedValueOnce({
        models: ['openai/gpt-5.5'],
        variants: { 'openai/gpt-5.5': ['high'] },
        complete: true,
      })
    resources.opencodeModelDiscovery = discovery
    const connected = deferred<void>()
    connect.mockImplementation(async () => connected.resolve())
    const controller = new AbortController()
    const host = new RunnerHost({
      ...hostOptions(),
      pollIntervalMs: QUIET_INTERVAL_MS,
    })
    const run = host.run(controller.signal)
    try {
      await connected.promise
      await vi.advanceTimersByTimeAsync(0)
      expect(discovery).toHaveBeenCalledTimes(1)

      await vi.advanceTimersByTimeAsync(4_999)
      expect(discovery).toHaveBeenCalledTimes(1)
      await vi.advanceTimersByTimeAsync(1)
      expect(discovery).toHaveBeenCalledTimes(2)
      expect(heartbeat.mock.calls.at(-1)?.[0]).toMatchObject({
        runtimeCatalogs: {
          opencode: {
            models: ['openai/gpt-5.5'],
            variants: { 'openai/gpt-5.5': ['high'] },
          },
        },
      })
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

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

  it('keeps polling for new work after an unowned runtime has cooled down', async (resources) => {
    const installed = installFakeOpenCodeRuntimeFactory(resources)
    const connected = deferred<void>()
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    poll.mockResolvedValue([])
    const controller = new AbortController()
    const host = new RunnerHost({
      ...hostOptions(),
      runtimeIdleGraceMs: 50,
    })
    const run = host.run(controller.signal)
    try {
      await connected.promise
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      expect(installed.lastRuntime?.ready()).toBe(true)
      await vi.advanceTimersByTimeAsync(50)
      expect(installed.lastRuntime?.ready()).toBe(false)
      const callsAfterCooling = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 2)
      expect(poll.mock.calls.length).toBeGreaterThan(callsAfterCooling)
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('ready-claim: registers the Pi model catalog snapshot', async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    const connected = deferred<void>()
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await connected.promise
      const connectArg = connect.mock.calls[0]?.[0] as Record<string, unknown>
      expect(connectArg).toMatchObject({
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
      expect(connectArg?.actionCatalog).toEqual(expectedActionCatalog())
      expect(JSON.stringify(connectArg?.actionCatalog)).not.toContain('run')
      expect(JSON.stringify(connectArg?.actionCatalog)).not.toContain('private')
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('does not publish an authoritative Pi catalog before discovery has loaded', async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    resources.piRuntimeFactory = () =>
      ({
        start: vi.fn(async () => ({
          ok: true as const,
          value: { ready: true, diagnostic: null, catalog: null },
          diagnostics: [],
        })),
        ready: () => true,
        diagnostic: () => null,
        catalog: () => null,
        shutdown: vi.fn(async () => undefined),
      }) as unknown as PiRuntime
    const connected = deferred<void>()
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await connected.promise
      const registration = connect.mock.calls[0]?.[0] as Record<string, unknown>
      expect(registration).toMatchObject({
        runtimeCatalogs: {
          opencode: { models: [], variants: {}, supportsReasoningEffort: false },
        },
      })
      expect((registration.runtimeCatalogs as Record<string, unknown>).pi).toBeUndefined()
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('RunnerRegistration registers the Pi model catalog', async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    const piCatalog = {
      models: [
        { provider: 'anthropic', id: 'claude-sonnet-4', thinkingLevels: ['off'] },
        { provider: 'openai', id: 'gpt-5.5', thinkingLevels: ['low', 'high'] },
      ],
    }
    const catalog = vi.fn(() => piCatalog)
    const piRuntime = {
      start: vi.fn(async () => ({
        ok: true as const,
        value: { ready: true, diagnostic: null, catalog: piCatalog },
        diagnostics: [],
      })),
      ready: () => true,
      diagnostic: () => null,
      catalog,
      shutdown: vi.fn(async () => undefined),
    } as unknown as PiRuntime
    resources.piRuntimeFactory = () => piRuntime
    const connected = deferred<void>()
    connect.mockImplementation(async () => {
      connected.resolve()
    })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await connected.promise
      const registration = connect.mock.calls[0]?.[0] as Record<string, unknown>
      expect(registration).toMatchObject({
        runtimeCatalogs: {
          pi: {
            models: ['anthropic/claude-sonnet-4', 'openai/gpt-5.5'],
            variants: {},
            reasoningEfforts: {
              'anthropic/claude-sonnet-4': ['off'],
              'openai/gpt-5.5': ['low', 'high'],
            },
            supportsReasoningEffort: true,
            complete: true,
          },
        },
      })
      expect(catalog).toHaveBeenCalledTimes(1)
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('RunnerRegistration carries the Pi model catalog on every heartbeat', async (resources) => {
    installFakeOpenCodeRuntimeFactory(resources)
    const connected = deferred<void>()
    connect.mockImplementation(async () => {
      connected.resolve()
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
      // Drive a heartbeat tick to confirm the registration body keeps
      // carrying the host-owned discovered snapshot.
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS + 1)
      const heartbeatBodies = heartbeat.mock.calls.map((call) => call[0] as Record<string, unknown>)
      expect(heartbeatBodies.length).toBeGreaterThan(0)
      for (const body of heartbeatBodies) {
        expect(body).toMatchObject({
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
        expect(body.actionCatalog).toEqual(expectedActionCatalog())
      }
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })

  it('runtime-not-ready: polling continues with a readiness witness while the existing report drains', async (resources) => {
    // Start with a ready runtime; let the first poll dispatch and
    // capture the work item's report; then simulate a server exit
    // and confirm polls continue with a negative readiness witness.
    const installed = installFakeOpenCodeRuntimeFactory(resources, { rebuildDelayMs: 50 })
    const reportStarted = deferred<void>()
    const reportRelease = deferred<void>()
    let reportAttempts = 0
    report.mockImplementation(async () => {
      reportAttempts += 1
      if (reportAttempts === 1) {
        reportStarted.resolve()
        await reportRelease.promise
      }
      return {}
    })
    blockingAction.mockReset().mockResolvedValue({ output: { message: 'ok' } })
    poll
      .mockResolvedValueOnce([
        {
          workflowRunId: 'wr-drain',
          workId: 'work-drain',
          workType: 'task',
          uses: 'test/block',
          ownerKind: 'workflow',
          variables: workflowVariables(),
        },
      ])
      .mockResolvedValue([])
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await reportStarted.promise
      // Flip the runtime to not-ready by simulating a server exit.
      installed.subscription.emit({ type: 'server.disconnected', payload: {} })
      expect(installed.lastRuntime?.ready()).toBe(false)
      // Capture the post-flip poll count; advance time and verify the
      // control-plane poll continues while the runtime is unavailable.
      const callsBefore = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBeGreaterThan(callsBefore)
      expect(poll.mock.calls.at(-1)?.[1]).toEqual(
        expect.objectContaining({
          runtimeReadiness: expect.arrayContaining([expect.objectContaining({ runtime: 'opencode', ready: false })]),
        }),
      )
      // awaitingAck drains while not-ready: the in-flight report
      // resolves and the entry leaves awaitingAck on the next loop
      // tick. The run continues without replaying the work.
      reportRelease.resolve()
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 2)
      // After rebuildDelayMs the runtime re-passes and the gate
      // reopens. Confirm the next poll tick runs.
      const callsAfterDrain = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBeGreaterThan(callsAfterDrain)
    } finally {
      controller.abort()
      reportRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it('server-exit-rebuild-resume: in-flight Workflow turns fail without auto-replay and claiming resumes after rebuild', async (resources) => {
    const installed = installFakeOpenCodeRuntimeFactory(resources, { rebuildDelayMs: 50 })
    const firstPollDone = deferred<void>()
    const actionStarted = deferred<void>()
    const actionRelease = deferred<void>()
    let pollCalls = 0
    poll.mockImplementation(async () => {
      pollCalls += 1
      if (pollCalls === 1) {
        firstPollDone.resolve()
        return [
          {
            workflowRunId: 'wr-exit',
            workId: 'work-exit',
            workType: 'task',
            uses: 'test/observe',
            ownerKind: 'workflow',
            variables: workflowVariables(),
          },
        ]
      }
      return []
    })
    blockingAction.mockReset().mockImplementation(async () => {
      actionStarted.resolve()
      await actionRelease.promise
      return { output: { message: 'ok' } }
    })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await firstPollDone.promise
      await actionStarted.promise
      // Mid-turn server exit: runtime goes not-ready and the in-flight
      // turn reports its result exactly once.
      installed.subscription.emit({ type: 'server.disconnected', payload: {} })
      expect(installed.lastRuntime?.ready()).toBe(false)
      // Confirm the runner keeps polling while not-ready, carrying the
      // negative witness instead of claiming new runtime work locally.
      const callsBefore = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 2)
      expect(poll.mock.calls.length).toBeGreaterThan(callsBefore)
      expect(poll.mock.calls.at(-1)?.[1]).toEqual(
        expect.objectContaining({
          runtimeReadiness: expect.arrayContaining([expect.objectContaining({ runtime: 'opencode', ready: false })]),
        }),
      )
      // Let the in-flight turn settle and report once (no replay).
      actionRelease.resolve()
      await vi.advanceTimersByTimeAsync(0)
      await vi.advanceTimersByTimeAsync(60)
      expect(installed.lastRuntime?.ready()).toBe(true)
      // After rebuild, claiming resumes.
      const callsAfterRebuild = poll.mock.calls.length
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBeGreaterThan(callsAfterRebuild)
      const reportsForExit = report.mock.calls.filter((call) => call[0]?.workId === 'work-exit')
      expect(reportsForExit.length).toBe(1)
    } finally {
      controller.abort()
      actionRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it('Workflow source does not receive the OpenCode runtime handle', async (resources) => {
    installReadyOpenCodeRuntimeFactory(resources)
    let observed: { openCodeRuntime: unknown } | null = null
    const actionStarted = deferred<void>()
    const actionRelease = deferred<void>()
    blockingAction.mockReset().mockImplementation(async (_inputs: unknown, context: { openCodeRuntime?: unknown }) => {
      observed = { openCodeRuntime: context.openCodeRuntime }
      actionStarted.resolve()
      await actionRelease.promise
      return { output: { message: 'ok' } }
    })
    poll
      .mockResolvedValueOnce([
        {
          workflowRunId: 'wr-workflow',
          workId: 'work-workflow',
          workType: 'task',
          uses: 'test/observe',
          ownerKind: 'workflow',
          variables: workflowVariables(),
        },
      ])
      .mockResolvedValue([])
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      await actionStarted.promise
      const observedNonNull = observed as { openCodeRuntime: unknown } | null
      expect(observedNonNull).not.toBeNull()
      expect(observedNonNull?.openCodeRuntime).toBeUndefined()
    } finally {
      controller.abort()
      actionRelease.resolve()
      await run.catch(() => undefined)
    }
  })

  it('AgentJob path drives the AgentJobExecutor, not the action registry', async (resources) => {
    installReadyOpenCodeRuntimeFactory(resources)
    // Verify the source-keyed dispatch wiring at the executor
    // boundary directly: an AgentJob ownerKind resolves through
    // the AgentJobExecutor entry instead of the action registry.
    // The full run-loop wiring is exercised by
    // `tests/agent-job-executor.spec.ts`; here we just need to
    // confirm the executor branches on owner-kind BEFORE the
    // action registry is consulted.
    let registryInvoked = false
    blockingAction.mockReset().mockImplementation(async () => {
      registryInvoked = true
      return { status: 'success', message: 'should-not-reach' }
    })
    // Use the WorkExecutor directly so we don't drive the run loop.
    const { WorkExecutor } = await import('../src/runtime/executor.js')
    const { AgentJobExecutor } = await import('../src/runtime/agent-job-executor.js')
    const fakeRuntime = {
      ready: () => true,
      diagnostic: () => null,
      async runTurn() {
        return {
          ok: true,
          value: {
            facts: { finalAssistantText: 'agent done', runtimeSessionId: 'ses_x', workDir: '/virtual/agent-job' },
            diagnostics: [],
          },
          diagnostics: [],
        }
      },
    } as never
    const executor = new WorkExecutor(
      {
        resolve: (uses?: string | null) => {
          if (uses === 'test/observe') return blockingAction
          return undefined
        },
      } as never,
      {
        async prepare() {
          return { path: '/virtual/agent-job', branch: null, changeDir: null }
        },
      } as never,
      {
        async attachAgentSession() {
          return undefined
        },
        async getAgentSession() {
          return null
        },
      } as never,
      '/virtual/agent-job',
      undefined,
      fakeRuntime,
      new AgentJobExecutor({} as never, { openCode: fakeRuntime, pi: null }),
    )
    const result = await executor.execute(
      {
        workflowRunId: '',
        workId: 'aj-1',
        workType: 'task',
        ownerKind: 'agent-job',
        agentJobId: 'aj-1',
        with: { prompt: 'do the agent-job thing', runtime: 'opencode' },
        variables: { workspace: { path: '/virtual/agent-job', branch: null, changeDir: null } },
      },
      new AbortController().signal,
    )
    expect(result.status).toBe('completed')
    expect(registryInvoked).toBe(false)
  })

  it('runtime-not-ready: AgentJob polls continue while the server admission fence rejects the claim', async (resources) => {
    // Use a long rebuild delay so the negative witness stays present
    // throughout the post-flip observation window. The poll mock returns the AgentJob dispatch
    // exactly once followed by empty arrays so the dispatch loop
    // can't tight-loop on the same work key (#410 T-001: the
    // AgentJobExecutor closes the work within a few microtasks, so
    // awaitingAck is empty before the next poll tick).
    const installedHandles = installFakeOpenCodeRuntimeFactory(resources, { rebuildDelayMs: 60_000 })
    poll
      .mockResolvedValueOnce([
        {
          workflowRunId: '',
          workId: 'work-agent-job',
          workType: 'task',
          uses: 'test/observe',
          ownerKind: 'agent-job',
          agentJobId: 'aj-1',
          variables: { workspace: { path: '/virtual/mohist-runner-host-opencode-runtime' } },
        },
      ])
      .mockResolvedValue([])
    blockingAction.mockReset().mockResolvedValue({ output: { message: 'ok' } })
    const controller = new AbortController()
    const host = hostWithFakeTerminalDelivery()
    const run = host.run(controller.signal)
    try {
      const runtime = await installedHandles.runtimeCreated
      expect(installedHandles.lastRuntime).toBe(runtime)
      // Drive the run loop until the first poll fires.
      for (let i = 0; i < 30 && poll.mock.calls.length === 0; i += 1) {
        await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)
      }
      const callsBeforeFlip = poll.mock.calls.length
      expect(callsBeforeFlip).toBeGreaterThan(0)
      // Flip the runtime to not-ready. The server-side admission fence
      // rejects runtime-specific claims while polling stays alive. The subscription lives on the fake
      // handles returned by `installFakeOpenCodeRuntimeFactory` — not
      // on the runtime instance itself, which only stores it as
      // private state.
      installedHandles.subscription.emit({ type: 'server.disconnected', payload: {} })
      expect(installedHandles.lastRuntime?.ready()).toBe(false)
      // Drive timers for a few intervals; the poll mock continues to
      // receive the negative readiness witness.
      await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS * 4)
      expect(poll.mock.calls.length).toBeGreaterThan(callsBeforeFlip)
      expect(poll.mock.calls.at(-1)?.[1]).toEqual(
        expect.objectContaining({
          runtimeReadiness: expect.arrayContaining([expect.objectContaining({ runtime: 'opencode', ready: false })]),
        }),
      )
    } finally {
      controller.abort()
      await run.catch(() => undefined)
    }
  })
})
