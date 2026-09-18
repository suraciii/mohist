import { AsyncLocalStorage } from 'node:async_hooks'
import { join } from 'node:path'
import { verifyOnlyNamedWorkspaceManager } from './support/workspace-mock.js'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { RunnerHost } from '../src/runtime/host.js'
import type { PolledDispatch } from '../src/core/types.js'
import type { ActionDefinition } from '../src/actions/manifest.js'
import { ActionRegistry } from '../src/actions/registry.js'
import { deferred } from './support/deferred.js'
import type { GitRunner } from '../src/runtime/git-probe.js'
import { withTestRunnerResources } from './support/test-resources.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'
import type { RunnerFileSystem } from '../src/system/filesystem.js'
import type { ExternalProcessPolicy } from '../src/system/process-policy.js'
import type { RunnerLogger } from '../src/system/logger.js'
import { createLoggerCapture } from './support/logger-test.js'
import type { PiRuntime } from '../src/runtime/pi/index.js'
import type { PiRuntimeFactory } from '../src/runtime/pi/factory.js'
import type { CodexCatalog, CodexRuntime, CodexRuntimeDeps, CodexRuntimeFactory } from '../src/runtime/codex/index.js'
import { parseEnabledAgentRuntimes } from '../src/runtime/enabled-agent-runtimes.js'

const POLL_INTERVAL_MS = 10
const QUIET_INTERVAL_MS = 60_000
const HEARTBEAT_QUIET_MS = 3_600_000
const CODEX_MODEL_DISCOVERY_MS = 60_000
const RUNNER_ROOT = '/virtual/mohist-runner-host-codex-runtime'
const RUNNER_WORKSPACE_PATH = '/virtual/runner-workspace'

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

function createHostMocks(): HostMocks {
  return {
    connect: vi.fn(async () => undefined),
    heartbeat: vi.fn(async () => undefined),
    disconnect: vi.fn(async () => undefined),
    poll: vi.fn(async (): Promise<PolledDispatch[]> => []),
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

interface HostTestResources {
  fileSystem: RunnerFileSystem
  gitRunner: GitRunner
  logger: RunnerLogger
  externalProcessPolicy: ExternalProcessPolicy
  piRuntimeFactory?: PiRuntimeFactory
  codexRuntimeFactory?: CodexRuntimeFactory
}

function fakePiRuntime(): PiRuntime {
  return {
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
    shutdown: async () => undefined,
  } as never
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
      piRuntimeFactory: () => fakePiRuntime(),
    }
    await withTestRunnerResources(async () => {
      await hostMockStorage.run({ mocks: createHostMocks() }, async () => {
        vi.useFakeTimers()
        try {
          await body(resources)
        } finally {
          vi.useRealTimers()
        }
      })
    }, resources)
  })
}

interface FakeCodexRuntimeHandles {
  factory: CodexRuntimeFactory
  deps: CodexRuntimeDeps[]
  stats: { start: number; shutdown: number; refresh: number }
  setCatalog(catalog: CodexCatalog | null): void
  setChanged(changed: boolean): void
  setReady(ready: boolean): void
}

function completeCatalog(revision = 'codex-rev-1'): CodexCatalog {
  return {
    models: [
      {
        id: 'gpt-5',
        displayName: 'GPT-5',
        reasoningEfforts: ['off', 'high'],
        defaultReasoningEffort: 'off',
        supportsReasoningEffort: true,
      },
    ],
    complete: true,
    capabilityRevision: revision,
  }
}

/**
 * Install a fake `CodexRuntime` through the factory seam. The runtime
 * records construction deps and lifecycle calls and lets a test drive
 * readiness, catalog content, and `refreshCatalog().changed` directly.
 */
function installFakeCodexRuntimeFactory(
  resources: HostTestResources,
  options: { catalog?: CodexCatalog | null; changed?: boolean; ready?: boolean; startOk?: boolean } = {},
): FakeCodexRuntimeHandles {
  let catalog = options.catalog === undefined ? completeCatalog() : options.catalog
  let changed = options.changed ?? true
  let ready = options.ready ?? true
  const startOk = options.startOk ?? true
  const deps: CodexRuntimeDeps[] = []
  const stats = { start: 0, shutdown: 0, refresh: 0 }
  const factory: CodexRuntimeFactory = (runtimeDeps) => {
    deps.push(runtimeDeps)
    const runtime = {
      ready: () => ready,
      generation: () => 1,
      diagnostic: () => null,
      catalog: () => catalog,
      async start() {
        stats.start += 1
        if (!startOk) {
          return {
            ok: false as const,
            error: { kind: 'unavailable-runtime' as const, message: 'no codex CLI', diagnostics: [] },
            diagnostics: [],
          }
        }
        return {
          ok: true as const,
          value: { ready, diagnostic: null, catalog, generation: 1 },
          diagnostics: [],
        }
      },
      async refreshCatalog() {
        stats.refresh += 1
        return { changed, catalog }
      },
      async shutdown() {
        stats.shutdown += 1
      },
    }
    return runtime as unknown as CodexRuntime
  }
  resources.codexRuntimeFactory = factory
  return {
    factory,
    deps,
    stats,
    setCatalog(value) {
      catalog = value
    },
    setChanged(value) {
      changed = value
    },
    setReady(value) {
      ready = value
    },
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

function baseHostOptions(): ConstructorParameters<typeof RunnerHost>[0] {
  return {
    serverUrl: 'https://runner.test',
    runnerId: 'runner-test',
    projectId: 'project-1',
    runnerRoot: RUNNER_ROOT,
    pollIntervalMs: POLL_INTERVAL_MS,
    heartbeatIntervalMs: QUIET_INTERVAL_MS,
    dispatchLivenessProbeIntervalMs: QUIET_INTERVAL_MS,
    namedWorkspaceManager: verifyOnlyNamedWorkspaceManager({ path: RUNNER_WORKSPACE_PATH, branch: null }),
  }
}

async function runHostToFirstPoll(
  options: ConstructorParameters<typeof RunnerHost>[0],
  body: (host: RunnerHost) => Promise<void>,
): Promise<void> {
  const connected = deferred<void>()
  const polled = deferred<void>()
  connect.mockImplementation(async () => {
    connected.resolve()
  })
  poll.mockImplementation(async () => {
    polled.resolve()
    return []
  })
  const controller = new AbortController()
  const host = new RunnerHost(options, runtimeActionRegistry())
  const run = host.run(controller.signal)
  try {
    await connected.promise
    await polled.promise
    await body(host)
  } finally {
    controller.abort()
    await run.catch(() => undefined)
  }
}

function expectedCodexEntry(revision: string) {
  return {
    models: ['gpt-5'],
    variants: {},
    reasoningEfforts: { 'gpt-5': ['off', 'high'] },
    supportsReasoningEffort: true,
    complete: true,
    capabilityRevision: revision,
  }
}

describe('RunnerHost wires the CodexRuntime lifecycle', () => {
  it('constructs, starts, publishes, and shuts down Codex through the factory when enabled', async (resources) => {
    const fake = installFakeCodexRuntimeFactory(resources)

    await runHostToFirstPoll(
      { ...baseHostOptions(), enabledAgentRuntimes: ['codex'], runtimeShutdownTimeoutMs: 4_321 },
      async () => {
        expect(fake.deps).toHaveLength(1)
        expect(fake.deps[0]).toMatchObject({
          codexHome: join(RUNNER_ROOT, '.mohist', 'codex'),
          cwd: RUNNER_ROOT,
          runtimeShutdownTimeoutMs: 4_321,
        })
        expect(fake.stats.start).toBe(1)

        const registration = connect.mock.calls[0]?.[0] as {
          runtimeCatalogs: Record<string, unknown>
          capabilities: string[]
        }
        expect(registration.runtimeCatalogs.codex).toEqual(expectedCodexEntry('codex-rev-1'))
        expect(registration.capabilities.filter((capability) => capability.startsWith('manager-'))).toEqual([])

        expect(poll.mock.calls[0]?.[1]).toMatchObject({
          runtimeReadiness: [{ runtime: 'codex', ready: true, generation: 1 }],
        })
      },
    )

    expect(fake.stats.shutdown).toBe(1)
    expect(fake.stats.refresh).toBeGreaterThanOrEqual(1)
  })

  it('does not construct Codex when it is not enabled and keeps the Pi-only registration', async (resources) => {
    const codexFactory = vi.fn(() => {
      throw new Error('Codex must not be constructed when disabled')
    })
    resources.codexRuntimeFactory = codexFactory

    await runHostToFirstPoll({ ...baseHostOptions(), enabledAgentRuntimes: ['pi'] }, async () => {
      expect(codexFactory).not.toHaveBeenCalled()
      const registration = connect.mock.calls[0]?.[0] as { runtimeCatalogs: Record<string, unknown> }
      expect(Object.keys(registration.runtimeCatalogs)).toEqual(['pi'])
      expect(registration.runtimeCatalogs.codex).toBeUndefined()
      expect(poll.mock.calls[0]?.[1]).toMatchObject({
        runtimeReadiness: [{ runtime: 'pi', ready: true, generation: 1 }],
      })
    })
  })

  it('keeps the Pi default and never constructs Codex when ENABLED_AGENT_RUNTIMES is unset', async (resources) => {
    expect([...parseEnabledAgentRuntimes(undefined)]).toEqual(['pi'])
    const codexFactory = vi.fn(() => {
      throw new Error('Codex must not be constructed by default')
    })
    resources.codexRuntimeFactory = codexFactory

    await runHostToFirstPoll(baseHostOptions(), async () => {
      expect(codexFactory).not.toHaveBeenCalled()
      const registration = connect.mock.calls[0]?.[0] as { runtimeCatalogs: Record<string, unknown> }
      expect(Object.keys(registration.runtimeCatalogs)).toEqual(['pi'])
    })
  })

  it('does not publish an incomplete or empty Codex catalog', async (resources) => {
    installFakeCodexRuntimeFactory(resources, {
      catalog: { models: [], complete: false, capabilityRevision: 'empty' },
      changed: false,
    })

    await runHostToFirstPoll({ ...baseHostOptions(), enabledAgentRuntimes: ['codex'] }, async () => {
      const registration = connect.mock.calls[0]?.[0] as { runtimeCatalogs: Record<string, unknown> }
      expect(registration.runtimeCatalogs.codex).toBeUndefined()
    })
  })

  it('does not publish an empty but complete Codex catalog', async (resources) => {
    installFakeCodexRuntimeFactory(resources, {
      catalog: { models: [], complete: true, capabilityRevision: 'complete-empty' },
      changed: false,
    })

    await runHostToFirstPoll({ ...baseHostOptions(), enabledAgentRuntimes: ['codex'] }, async () => {
      const registration = connect.mock.calls[0]?.[0] as { runtimeCatalogs: Record<string, unknown> }
      expect(registration.runtimeCatalogs.codex).toBeUndefined()
    })
  })

  it('heartbeats immediately only when the Codex catalog snapshot changes', async (resources) => {
    const fake = installFakeCodexRuntimeFactory(resources, { changed: true })

    await runHostToFirstPoll(
      {
        ...baseHostOptions(),
        enabledAgentRuntimes: ['codex'],
        heartbeatIntervalMs: HEARTBEAT_QUIET_MS,
        modelRediscoveryIntervalMs: CODEX_MODEL_DISCOVERY_MS,
      },
      async () => {
        // The startup maintenance pass found a changed snapshot and asked
        // for an immediate registration heartbeat.
        await vi.advanceTimersByTimeAsync(0)
        const initialHeartbeats = heartbeat.mock.calls.length
        expect(initialHeartbeats).toBeGreaterThanOrEqual(1)
        const latest = heartbeat.mock.calls.at(-1)?.[0] as { runtimeCatalogs: Record<string, unknown> }
        expect(latest.runtimeCatalogs.codex).toEqual(expectedCodexEntry('codex-rev-1'))

        // An unchanged refresh must not emit another heartbeat.
        fake.setChanged(false)
        await vi.advanceTimersByTimeAsync(CODEX_MODEL_DISCOVERY_MS)
        expect(heartbeat.mock.calls.length).toBe(initialHeartbeats)

        // A changed snapshot reactivates the immediate heartbeat with the
        // new capability revision.
        fake.setCatalog(completeCatalog('codex-rev-2'))
        fake.setChanged(true)
        await vi.advanceTimersByTimeAsync(CODEX_MODEL_DISCOVERY_MS)
        expect(heartbeat.mock.calls.length).toBeGreaterThan(initialHeartbeats)
        const next = heartbeat.mock.calls.at(-1)?.[0] as { runtimeCatalogs: Record<string, unknown> }
        expect(next.runtimeCatalogs.codex).toEqual(expectedCodexEntry('codex-rev-2'))
      },
    )
  })
})
