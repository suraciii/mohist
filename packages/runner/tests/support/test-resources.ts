import { AsyncLocalStorage } from "node:async_hooks"
import { withRunnerResources, type RunnerFileSystem, type RunnerResourceContext } from "../../src/system/filesystem.js"
import type { GitRunner } from "../../src/runtime/git-probe.js"
import type { PiRuntime } from "../../src/runtime/pi/index.js"
import type { PiRuntimeFactory } from "../../src/runtime/pi/factory.js"
import type { ExternalProcessPolicy } from "../../src/system/process-policy.js"
import type { RunnerLogger } from "../../src/system/logger.js"
import { installReadyOpenCodeRuntimeFactory, type OpenCodeRuntimeTestResources } from "./opencode-runtime-factory.js"
import { createLoggerCapture } from "./logger-test.js"
import { MemoryFileSystem } from "./memory-filesystem.js"

interface TestResourceState {
  readonly fileSystem: RunnerFileSystem
  readonly tempDirs: string[]
  nextTempId: number
}

const testResourceStorage = new AsyncLocalStorage<TestResourceState>()
const missingBuildInfoFileSystem = {
  exists: () => false,
  readText: (path: string) => { throw new Error(`unexpected build info read: ${path}`) },
}
const emptyEnvironment: Readonly<Record<string, string | undefined>> = Object.freeze({})
const denyExternalProcess: ExternalProcessPolicy = {
  assertAllowed(label) { throw new Error(`external process forbidden in runner test: ${label}`) },
  register() {},
}
const denyTransport = {
  fetch: async () => { throw new Error("network transport forbidden in runner test") },
}

export function currentTestResourceState(): TestResourceState {
  const state = testResourceStorage.getStore()
  if (!state) throw new Error("runner test resource context is not active")
  return state
}

export async function withTestRunnerResources<T>(
  body: (fileSystem: RunnerFileSystem) => Promise<T>,
  resources: Omit<RunnerResourceContext, "fileSystem"> & { fileSystem?: RunnerFileSystem } = {},
): Promise<T> {
  const fileSystem = resources.fileSystem ?? new MemoryFileSystem()
  const state: TestResourceState = { fileSystem, tempDirs: [], nextTempId: 1 }
  const logger = resources.logger ?? createLoggerCapture()
  const scopedResources: RunnerResourceContext = new Proxy(resources, {
    get(target, property, receiver) {
      if (property === "fileSystem") return fileSystem
      if (property === "logger") return target.logger ?? logger
      if (property === "buildInfoFileSystem") return target.buildInfoFileSystem ?? missingBuildInfoFileSystem
      if (property === "environment") return target.environment ?? emptyEnvironment
      if (property === "externalProcessPolicy") return target.externalProcessPolicy ?? denyExternalProcess
      if (property === "transport") return target.transport ?? denyTransport
      return Reflect.get(target, property, receiver)
    },
  })
  return await withRunnerResources(scopedResources, async () => {
    return await testResourceStorage.run(state, async () => {
      try {
        return await body(fileSystem)
      } finally {
        await cleanupTestTempDirs()
      }
    })
  })
}

export interface DefaultRunnerTestResources extends OpenCodeRuntimeTestResources {
  fileSystem: RunnerFileSystem
  logger: RunnerLogger
  externalProcessPolicy: ExternalProcessPolicy
  piRuntimeFactory: PiRuntimeFactory
  gitRunner: GitRunner
}

export function createDefaultRunnerTestResources(): DefaultRunnerTestResources {
  return {
    fileSystem: new MemoryFileSystem(),
    logger: createLoggerCapture(),
    externalProcessPolicy: {
      assertAllowed(label) {
        throw new Error(`external process forbidden in runner test: ${label}`)
      },
      register() {},
    },
    piRuntimeFactory: () => ({
      start: async () => ({ ok: true, value: { ready: true, diagnostic: null, catalog: { models: [] } }, diagnostics: [] }),
      ready: () => true,
      diagnostic: () => null,
      catalog: () => ({ models: [] }),
      shutdown: async () => {},
    } as never),
    gitRunner: async () => ({
      success: false,
      exitCode: 128,
      stdout: "",
      stderr: "not a git repository",
      combinedOutput: "not a git repository",
    }),
  }
}

export async function withDefaultRunnerTestResources<T>(
  body: (resources: DefaultRunnerTestResources) => Promise<T>,
): Promise<T> {
  const resources = createDefaultRunnerTestResources()
  return await withTestRunnerResources(async () => {
    installReadyOpenCodeRuntimeFactory(resources)
    return await body(resources)
  }, resources)
}

export function registerTestTempDir(path: string): void {
  currentTestResourceState().tempDirs.push(path)
}

export async function cleanupTestTempDirs(): Promise<void> {
  const state = currentTestResourceState()
  const errors: unknown[] = []
  while (state.tempDirs.length > 0) {
    const path = state.tempDirs.pop()!
    try {
      await state.fileSystem.deleteDirectory(path)
      if (state.fileSystem.exists(path)) throw new Error(`test temp directory still exists after cleanup: ${path}`)
    } catch (error) {
      if (isAbsentPathError(error)) continue
      errors.push(error)
    }
  }
  if (errors.length === 1) throw errors[0]
  if (errors.length > 1) throw new AggregateError(errors, "Failed to clean test temp directories")
}

function isAbsentPathError(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT"
}
