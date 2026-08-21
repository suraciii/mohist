import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { chmod, mkdir, rm, writeFile } from 'node:fs/promises'
import { createServer, type Server, type Socket } from 'node:net'
import { join } from 'node:path'
import { runCommand } from '../system/process.js'
import { CredentialMasker } from './task-log.js'
import { createDefaultOpenCodeRuntime } from './opencode/factory.js'
import type { OpenCodeRuntime } from './opencode/runtime.js'
import { createIsolatedOpencodeServer } from './opencode/server-process.js'

export interface ManagerExecutionGrant {
  readonly managementCredential: string
  readonly replyCredential: string
  readonly executionId: string
  readonly expiresAt: string
  readonly deploymentEpoch: string
}

export interface ManagerBashOptions {
  readonly onData: (data: Buffer) => void
  readonly signal?: AbortSignal
  readonly timeout?: number
  readonly env?: NodeJS.ProcessEnv
}

/**
 * One in-memory Manager process boundary. The grant is deliberately not part
 * of DispatchWorkItem and this class is never passed to a journal or report.
 */
export class ManagerExecutionBoundary {
  readonly masker = new CredentialMasker()
  private readonly baseEnvironment: NodeJS.ProcessEnv
  private readonly socketPath: string
  private readonly directory: string
  private readonly grant: ManagerExecutionGrant
  private broker: Server | null = null
  private isolatedOpenCodeRuntime: OpenCodeRuntime | null = null
  private disposed = false

  private constructor(
    grant: ManagerExecutionGrant,
    directory: string,
    socketPath: string,
    baseEnvironment: NodeJS.ProcessEnv,
  ) {
    this.grant = grant
    this.directory = directory
    this.socketPath = socketPath
    this.baseEnvironment = baseEnvironment
    this.masker.registerSecret(grant.managementCredential)
    this.masker.registerSecret(grant.replyCredential)
  }

  static async create(grant: ManagerExecutionGrant, runnerRoot: string): Promise<ManagerExecutionBoundary> {
    if (!grant.managementCredential || !grant.replyCredential || !grant.executionId) {
      throw new Error('Manager execution grant is incomplete')
    }
    if (!Number.isFinite(Date.parse(grant.expiresAt)) || Date.now() >= Date.parse(grant.expiresAt)) {
      throw new Error('Manager execution grant is expired or malformed')
    }
    const suffix = createHash('sha256')
      .update(`${grant.executionId}\n${grant.deploymentEpoch}\n${Math.random()}`)
      .digest('hex')
      .slice(0, 32)
    const directory = join(runnerRoot, 'manager-executions', suffix)
    await mkdir(directory, { recursive: true, mode: 0o700 })
    const socketPath = join(directory, 'broker.sock')
    const boundary = new ManagerExecutionBoundary(grant, directory, socketPath, {
      ...process.env,
      MOHIST_MANAGER_MODE: '1',
    })
    await boundary.writeLauncher()
    await boundary.startBroker()
    return boundary
  }

  /** No bearer is present in this environment. */
  environment(): NodeJS.ProcessEnv {
    if (this.disposed) throw new Error('Manager execution boundary is closed')
    const currentPath = this.baseEnvironment.PATH ?? ''
    return {
      ...this.baseEnvironment,
      PATH: `${this.directory}${process.platform === 'win32' ? ';' : ':'}${currentPath}`,
      MOHIST_MANAGER_MODE: '1',
      MOHIST_MANAGER_BROKER: this.socketPath,
      MOHIST_MANAGER_EXECUTION_ID: this.grant.executionId,
    }
  }

  /**
   * Pi's real Bash tool calls this operation. Generic commands inherit only
   * the non-secret broker locator; the launcher is the sole code path that
   * receives a bearer from the broker.
   */
  bashOperations() {
    return {
      exec: async (command: string, cwd: string, options: ManagerBashOptions) => {
        if (this.expired()) throw new Error('Manager execution grant expired')
        const result = await runCommand(
          'bash',
          ['-lc', command],
          cwd,
          options.signal ?? new AbortController().signal,
          { ...this.environment(), ...(options.env ?? {}) },
          {
            timeoutMs: options.timeout,
            onLine: (line) => options.onData(Buffer.from(`${this.mask(line)}\n`, 'utf8')),
          },
        )
        return { exitCode: result.exitCode }
      },
    }
  }

  async openCodeRuntime(workDir: string, signal: AbortSignal): Promise<OpenCodeRuntime | null> {
    if (this.disposed || this.expired()) return null
    if (this.isolatedOpenCodeRuntime) return this.isolatedOpenCodeRuntime
    const runtime = createDefaultOpenCodeRuntime({
      directory: workDir,
      idleGraceMs: 0,
      serverFactory: (directory, startSignal, options) =>
        createIsolatedOpencodeServer(directory, startSignal, {
          ...options,
          environment: this.environment(),
        }),
    })
    const started = await runtime.start(signal)
    if (!started.ok) {
      await runtime.shutdown().catch(() => undefined)
      return null
    }
    this.isolatedOpenCodeRuntime = runtime
    return runtime
  }

  hasExpired(): boolean {
    return this.expired()
  }

  private expired(): boolean {
    return Date.now() >= Date.parse(this.grant.expiresAt)
  }

  mask(value: string): string {
    return this.masker.mask(value)
  }

  redact(value: unknown): unknown {
    if (typeof value === 'string') return this.mask(value)
    if (Array.isArray(value)) return value.map((item) => this.redact(item))
    if (value && typeof value === 'object') {
      return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, this.redact(item)]))
    }
    return value
  }

  async dispose(): Promise<void> {
    if (this.disposed) return
    this.disposed = true
    const runtime = this.isolatedOpenCodeRuntime
    this.isolatedOpenCodeRuntime = null
    await runtime?.shutdown().catch(() => undefined)
    await new Promise<void>((resolve) => {
      const broker = this.broker
      this.broker = null
      if (!broker || !broker.listening) {
        resolve()
        return
      }
      broker.close(() => resolve())
    }).catch(() => undefined)
    await rm(this.directory, { recursive: true, force: true }).catch(() => undefined)
  }

  private async writeLauncher(): Promise<void> {
    const launcher = join(this.directory, 'mo')
    const source = `#!/usr/bin/env node
const net = require('node:net')
const { spawn } = require('node:child_process')
const path = require('node:path')
const broker = process.env.MOHIST_MANAGER_BROKER
if (!broker) process.exit(126)
const args = process.argv.slice(2)
const kind = args[0] === 'slack' && args[1] === 'message' && args[2] === 'send' ? 'reply' : 'management'
const pathParts = (process.env.PATH || '').split(path.delimiter).filter((item) => item && path.resolve(item) !== path.dirname(process.argv[1]))
const realPath = pathParts.map((item) => path.join(item, process.platform === 'win32' ? 'mo.exe' : 'mo')).find((item) => require('node:fs').existsSync(item))
if (!realPath) process.exit(127)
const socket = net.createConnection(broker)
let body = ''
socket.on('data', (chunk) => { body += chunk.toString() })
socket.on('error', () => process.exit(126))
socket.on('end', () => {
  let response
  try { response = JSON.parse(body) } catch { process.exit(126); return }
  if (!response || typeof response.credential !== 'string') { process.exit(126); return }
  const env = { ...process.env }
  delete env.MOHIST_MANAGER_BROKER
  delete env.MOHIST_MANAGER_EXECUTION_ID
  if (kind === 'reply') env.MOHIST_MANAGER_REPLY_TOKEN = response.credential
  else env.MOHIST_MANAGER_MANAGEMENT_TOKEN = response.credential
  const child = spawn(realPath, args, { cwd: process.cwd(), env, stdio: 'inherit' })
  child.on('exit', (code) => process.exit(code === null ? 1 : code))
  child.on('error', () => process.exit(126))
})
socket.end(JSON.stringify({ kind, launcherPid: process.pid }))
`
    await writeFile(launcher, source, { encoding: 'utf8', mode: 0o700 })
    if (process.platform !== 'win32') await chmod(launcher, 0o700)
  }

  private async startBroker(): Promise<void> {
    if (process.platform === 'win32') {
      throw new Error('Manager execution broker requires the named-pipe adapter on Windows')
    }
    const broker = createServer((socket) => this.handleConnection(socket))
    await new Promise<void>((resolve, reject) => {
      broker.once('error', reject)
      broker.listen(this.socketPath, () => {
        broker.removeListener('error', reject)
        resolve()
      })
    })
    this.broker = broker
  }

  private handleConnection(socket: Socket): void {
    let body = ''
    socket.setEncoding('utf8')
    socket.on('data', (chunk) => {
      body += chunk
      if (body.length > 256) socket.destroy()
    })
    socket.on('end', () => {
      let request: { kind?: unknown; launcherPid?: unknown }
      try {
        request = JSON.parse(body) as { kind?: unknown; launcherPid?: unknown }
      } catch {
        socket.end('{}')
        return
      }
      if ((request.kind !== 'management' && request.kind !== 'reply')
        || !this.isLauncherProcess(request.launcherPid)) {
        socket.end('{}')
        return
      }
      if (this.expired()) {
        socket.end('{}')
        return
      }
      const credential = request.kind === 'reply' ? this.grant.replyCredential : this.grant.managementCredential
      socket.end(JSON.stringify({ credential }))
    })
  }

  private isLauncherProcess(value: unknown): value is number {
    if (process.platform === 'win32' || typeof value !== 'number' || !Number.isSafeInteger(value) || value <= 0)
      return false
    try {
      const commandLine = readFileSync(`/proc/${value}/cmdline`, 'utf8')
        .split('\0')
        .filter((part) => part.length > 0)
      return commandLine.includes(join(this.directory, 'mo'))
    } catch {
      return false
    }
  }
}

export interface ManagerRuntimeProcessEnvironment {
  readonly environment: NodeJS.ProcessEnv
  readonly mask: (value: string) => string
}

export function managerRuntimeProcessEnvironment(boundary: ManagerExecutionBoundary): ManagerRuntimeProcessEnvironment {
  return {
    environment: boundary.environment(),
    mask: (value) => boundary.mask(value),
  }
}
