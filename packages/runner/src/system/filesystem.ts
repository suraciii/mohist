import { constants } from 'node:fs'
import {
  mkdir,
  open,
  readdir,
  readFile,
  realpath,
  rename,
  rm,
  stat,
  lstat,
  symlink,
  writeFile,
  appendFile,
  cp,
} from 'node:fs/promises'
import { AsyncLocalStorage } from 'node:async_hooks'
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import type { SpawnOptions, ChildProcessWithoutNullStreams } from 'node:child_process'
import type { GitOptions } from '../actions/git.js'
import type { CommandLineOptions } from './process.js'
import type { RunnerLogger } from './logger.js'
import type { ExternalProcessPolicy } from './process-policy.js'
import type { PiRuntimeFactory } from '../runtime/pi/factory.js'
import type { OpenCodeRuntimeFactory } from '../runtime/opencode/factory.js'
import type {
  OpencodeLogFileSystem,
  OpencodeProviderErrorDiagnosticFinder,
} from '../runtime/opencode-log-diagnostics.js'
import type { LockHolderProbe } from '../runtime/worktree-enforcement.js'

export interface RunnerFileInfo {
  kind: 'file' | 'directory' | 'symlink'
  size: number
  mtimeMs: number
  isFile(): boolean
  isDirectory(): boolean
  isSymbolicLink(): boolean
}

export interface RunnerDirectoryEntry {
  name: string
  isFile(): boolean
  isDirectory(): boolean
  isSymbolicLink(): boolean
}

export interface RunnerDirectoryHandle {
  readonly path: string
  close(): Promise<void>
}

export interface RunnerGitResult {
  success: boolean
  stdout: string
  stderr: string
  exitCode: number
  combinedOutput: string
  status?: 'timeout'
  timeoutMs?: number
}

export type RunnerGitRunner = (
  workDir: string,
  args: string[],
  signal: AbortSignal,
  options?: GitOptions,
) => Promise<RunnerGitResult>

export type RunnerProcessSpawner = (
  command: string,
  args: string[],
  options: SpawnOptions,
) => ChildProcessWithoutNullStreams
export type RunnerProcessKiller = (pid: number, signal?: NodeJS.Signals) => void
export type RunnerCommandRunner = (
  command: string,
  args: string[],
  cwd: string,
  signal: AbortSignal,
  env?: NodeJS.ProcessEnv,
  options?: CommandLineOptions,
) => Promise<{
  exitCode: number
  stdout: string
  stderr: string
  status?: 'timeout'
  timeoutMs?: number
}>

export interface RunnerArchiveFileSystem {
  exists(path: string): Promise<boolean>
  hasFiles(path: string): Promise<boolean>
  ensureDirectory(path: string): Promise<void>
  moveDirectory(source: string, destination: string): Promise<void>
  readText(path: string): Promise<string>
  writeAtomic(path: string, content: string): Promise<void>
  remove(path: string): Promise<void>
}

export interface RunnerFileSystem {
  readonly supportsDirectoryHandles?: boolean
  readonly openDirectory?: (path: string, flags: number) => Promise<RunnerDirectoryHandle>
  exists(path: string): boolean
  ensureDir(path: string): Promise<void>
  readText(path: string): Promise<string>
  readBinary(path: string): Promise<Uint8Array>
  writeText(path: string, content: string, options?: { mode?: number }): Promise<void>
  writeBinary(path: string, content: Uint8Array): Promise<void>
  appendText(path: string, content: string): Promise<void>
  deleteFile(path: string): Promise<void>
  deleteDirectory(path: string): Promise<void>
  rename(source: string, destination: string): Promise<void>
  lstat(path: string): Promise<RunnerFileInfo>
  stat(path: string): Promise<RunnerFileInfo>
  readdir(path: string): Promise<RunnerDirectoryEntry[]>
  realpath(path: string): Promise<string>
  readTail(path: string, start: number, length: number): Promise<string>
  copyDirectory(source: string, destination: string): Promise<void>
  symlink(target: string, path: string): Promise<void>
}

function nodeFileInfo(value: {
  isFile(): boolean
  isDirectory(): boolean
  isSymbolicLink(): boolean
  size: number
  mtimeMs: number
}): RunnerFileInfo {
  const kind = value.isSymbolicLink() ? 'symlink' : value.isDirectory() ? 'directory' : 'file'
  return {
    kind,
    size: value.size,
    mtimeMs: value.mtimeMs,
    isFile: () => kind === 'file',
    isDirectory: () => kind === 'directory',
    isSymbolicLink: () => kind === 'symlink',
  }
}

export const nodeRunnerFileSystem: RunnerFileSystem = {
  supportsDirectoryHandles: true,
  openDirectory: async (path, flags) => {
    const handle = await open(path, flags)
    return {
      path: `/proc/${process.pid}/fd/${handle.fd}`,
      close: async () => await handle.close(),
    }
  },
  exists: (path) => existsSync(path),
  ensureDir: async (path) => {
    await mkdir(path, { recursive: true })
  },
  readText: async (path) => await readFile(path, 'utf8'),
  readBinary: async (path) => new Uint8Array(await readFile(path)),
  writeText: async (path, content, options) => {
    await mkdir(dirname(path), { recursive: true })
    await writeFile(path, content, options)
  },
  writeBinary: async (path, content) => {
    await mkdir(dirname(path), { recursive: true })
    await writeFile(path, content)
  },
  appendText: async (path, content) => {
    await mkdir(dirname(path), { recursive: true })
    await appendFile(path, content)
  },
  deleteFile: async (path) => await rm(path, { force: true }),
  deleteDirectory: async (path) => await rm(path, { recursive: true, force: true }),
  rename: async (source, destination) => await rename(source, destination),
  lstat: async (path) => nodeFileInfo(await lstat(path)),
  stat: async (path) => nodeFileInfo(await stat(path)),
  readdir: async (path) => {
    const entries = await readdir(path, { withFileTypes: true })
    return entries.map((entry) => ({
      name: entry.name,
      isFile: () => entry.isFile(),
      isDirectory: () => entry.isDirectory(),
      isSymbolicLink: () => entry.isSymbolicLink(),
    }))
  },
  realpath: async (path) => await realpath(path),
  readTail: async (path, start, length) => {
    const handle = await open(path, 'r')
    try {
      const buffer = Buffer.alloc(length)
      await handle.read(buffer, 0, length, start)
      return buffer.toString('utf8')
    } finally {
      await handle.close()
    }
  },
  copyDirectory: async (source, destination) => await cp(source, destination, { recursive: true, force: true }),
  symlink: async (target, path) => {
    await mkdir(dirname(path), { recursive: true })
    await symlink(target, path)
    return
  },
}

export interface RunnerResourceContext {
  readonly environment?: Readonly<Record<string, string | undefined>>
  readonly fileSystem?: RunnerFileSystem
  readonly runnerCredentialFileSystem?: {
    mkdirSync(path: string, options?: { recursive?: boolean }): void
    readFileSync(path: string, encoding: 'utf8'): string
    writeFileSync(path: string, content: string, options?: { mode?: number }): void
  }
  readonly logger?: RunnerLogger
  readonly buildInfoFileSystem?: {
    exists(path: string): boolean
    readText(path: string): string
  }
  readonly externalProcessPolicy?: ExternalProcessPolicy
  readonly piRuntimeFactory?: PiRuntimeFactory
  readonly openCodeRuntimeFactory?: OpenCodeRuntimeFactory
  readonly opencodeProviderErrorDiagnosticFinder?: OpencodeProviderErrorDiagnosticFinder
  readonly opencodeLogFileSystem?: OpencodeLogFileSystem
  readonly executorLockHolderProbe?: LockHolderProbe
  readonly worktreeClock?: () => number
  readonly commandRunner?: {
    run(
      command: string,
      args: string[],
      cwd: string,
      signal: AbortSignal,
      env?: NodeJS.ProcessEnv,
      options?: unknown,
    ): Promise<unknown>
  }
  readonly issueFieldCommandRunner?: (
    command: string,
    args: string[],
    cwd: string,
    signal: AbortSignal,
  ) => Promise<{
    exitCode: number
    stdout: string
    stderr: string
  }>
  readonly githubPrGitRunner?: RunnerGitRunner
  readonly githubPrGhRunner?: RunnerCommandRunner
  readonly githubPrStatusGhRunner?: RunnerCommandRunner
  readonly githubPrChecksTiming?: { pollIntervalMs?: number; noChecksGraceMs?: number; unavailableRetryLimit?: number }
  readonly githubPrTransientRetry?: { limit?: number; backoffMs?: number }
  readonly transport?: { fetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> }
  readonly gitRunner?: RunnerGitRunner
  readonly deliveryGitRunner?: RunnerGitRunner
  readonly signalRGitRunner?: (
    command: string,
    args: string[],
    cwd: string,
    signal: AbortSignal,
    env?: NodeJS.ProcessEnv,
    options?: CommandLineOptions,
  ) => Promise<{
    exitCode: number
    stdout: string
    stderr: string
    status?: 'timeout'
    timeoutMs?: number
  }>
  readonly signalRExistsChecker?: (path: string) => boolean
  readonly processSpawner?: RunnerProcessSpawner
  readonly processKiller?: RunnerProcessKiller
  readonly openSpecGitRunner?: RunnerGitRunner
  readonly archiveFileSystem?: RunnerArchiveFileSystem
  readonly workspacePrepareGitRunner?: RunnerGitRunner
  readonly workspacePrepareExistsChecker?: (path: string) => boolean
  readonly rebaseGitRunner?: RunnerGitRunner
  readonly rebaseExistsChecker?: (path: string) => boolean
  readonly pushGitRunner?: RunnerGitRunner
  readonly cleanupAgentAction?: (
    host: import('../actions/host.js').ActionHost,
    withInput: import('../core/types.js').JsonObject,
  ) => Promise<import('../core/types.js').ActionResult>
}

const resourceStorage = new AsyncLocalStorage<RunnerResourceContext>()

export function currentRunnerResources(): RunnerResourceContext | undefined {
  return resourceStorage.getStore()
}

export function currentRunnerFileSystem(): RunnerFileSystem {
  return resourceStorage.getStore()?.fileSystem ?? nodeRunnerFileSystem
}

export async function withRunnerResources<T>(
  resources: RunnerResourceContext,
  operation: () => Promise<T>,
): Promise<T> {
  return await resourceStorage.run(resources, operation)
}

export function createNodeFileInfo(value: {
  isFile(): boolean
  isDirectory(): boolean
  isSymbolicLink(): boolean
  size: number
  mtimeMs: number
}): RunnerFileInfo {
  return nodeFileInfo(value)
}

export function currentRunnerTransport(): (input: RequestInfo | URL, init?: RequestInit) => Promise<Response> {
  return currentRunnerResources()?.transport?.fetch ?? fetch
}

export { constants }
