import { createHash } from 'node:crypto'
import { existsSync } from 'node:fs'
import { spawn, type ChildProcess } from 'node:child_process'
import { chmod, mkdir, rm, writeFile } from 'node:fs/promises'
import { createServer, type Server, type Socket } from 'node:net'
import { join } from 'node:path'
import { runCommand } from '../system/process.js'
import { CredentialMasker } from './task-log.js'
import { createDefaultOpenCodeRuntime } from './opencode/factory.js'
import type { OpenCodeRuntime } from './opencode/runtime.js'
import { createIsolatedOpencodeServer } from './opencode/server-process.js'
import {
  DEFAULT_MANAGER_REQUEST_LIMITS,
  isManagerUsageRequest,
  managerRequestKind,
  resolveManagerRequestCapability,
  type ManagerRequestLimits,
} from './manager-capability-surface.js'
import { isManagerLauncherConnection, isManagerProcessConnection } from './manager-launcher-auth.js'

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

export interface ManagerExecutionBoundaryOptions {
  /** Frozen working directory for credential-bearing CLI children. */
  readonly workDir?: string
  /** Overrides the resolved `mo` executable; tests inject a stub here. */
  readonly moExecutable?: string
  readonly requestLimits?: ManagerRequestLimits
  /** Bounded grace before SIGKILL during disposal. */
  readonly terminationTimeoutMs?: number
}

const DEFAULT_TERMINATION_TIMEOUT_MS = 5_000

// The broker uses Linux kernel peer information to admit only the generated
// launcher process. Capability confinement remains a second gate: bearer
// values exist only inside spawned CLI children, requests resolve through the
// Manager vocabulary, the working directory is frozen, and each kind has a
// bounded request budget. The Server independently enforces lease origin,
// route allowlist, and anchor validation for whatever those children send.
/**
 * One in-memory Manager process boundary. The grant is deliberately not part
 * of DispatchWorkItem and this class is never passed to a journal or report.
 */
export class ManagerExecutionBoundary {
  readonly masker = new CredentialMasker()
  private readonly baseEnvironment: NodeJS.ProcessEnv
  private readonly socketPath: string
  private readonly credentialBrokerPath: string
  private readonly launcherPath: string
  private readonly directory: string
  private readonly grant: ManagerExecutionGrant
  private readonly realMoPath: string
  private readonly frozenCwd: string
  private readonly requestLimits: ManagerRequestLimits
  private readonly terminationTimeoutMs: number
  private readonly children = new Set<ChildProcess>()
  private readonly sockets = new Set<Socket>()
  private readonly usedRequests: Record<'management' | 'reply', number> = { management: 0, reply: 0 }
  private broker: Server | null = null
  private credentialBroker: Server | null = null
  private isolatedOpenCodeRuntime: OpenCodeRuntime | null = null
  private disposed = false
  private runningChildren = 0
  private activeCredentialChildPid: number | null = null
  private activeCredentialKind: 'management' | 'reply' | null = null
  private authorizationInvalidated = false

  private constructor(
    grant: ManagerExecutionGrant,
    directory: string,
    socketPath: string,
    credentialBrokerPath: string,
    baseEnvironment: NodeJS.ProcessEnv,
    realMoPath: string,
    frozenCwd: string,
    requestLimits: ManagerRequestLimits,
    terminationTimeoutMs: number,
  ) {
    this.grant = grant
    this.directory = directory
    this.socketPath = socketPath
    this.credentialBrokerPath = credentialBrokerPath
    this.launcherPath = join(directory, 'mo')
    this.baseEnvironment = baseEnvironment
    this.realMoPath = realMoPath
    this.frozenCwd = frozenCwd
    this.requestLimits = requestLimits
    this.terminationTimeoutMs = terminationTimeoutMs
    this.masker.registerSecret(grant.managementCredential)
    this.masker.registerSecret(grant.replyCredential)
  }

  static async create(
    grant: ManagerExecutionGrant,
    runnerRoot: string,
    options: ManagerExecutionBoundaryOptions = {},
  ): Promise<ManagerExecutionBoundary> {
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
    const credentialBrokerPath = join(directory, 'credential-broker.sock')
    const baseEnvironment: NodeJS.ProcessEnv = {
      ...process.env,
      MOHIST_MANAGER_MODE: '1',
    }
    const realMoPath = options.moExecutable ?? findRealMoPath(directory, baseEnvironment.PATH ?? '')
    if (!realMoPath) throw new Error('The real mo executable could not be resolved')
    const boundary = new ManagerExecutionBoundary(
      grant,
      directory,
      socketPath,
      credentialBrokerPath,
      baseEnvironment,
      realMoPath,
      options.workDir ?? process.cwd(),
      options.requestLimits ?? DEFAULT_MANAGER_REQUEST_LIMITS,
      options.terminationTimeoutMs ?? DEFAULT_TERMINATION_TIMEOUT_MS,
    )
    await boundary.writeLauncher()
    await boundary.startBroker()
    return boundary
  }

  /** The locator is non-secret; bearer values never enter this environment. */
  environment(): NodeJS.ProcessEnv {
    if (this.disposed) throw new Error('Manager execution boundary is closed')
    const currentPath = this.baseEnvironment.PATH ?? ''
    return {
      ...this.baseEnvironment,
      PATH: `${this.directory}${process.platform === 'win32' ? ';' : ':'}${currentPath}`,
      MOHIST_MANAGER_MODE: '1',
      MOHIST_MANAGER_BROKER: this.socketPath,
      MOHIST_MANAGER_CREDENTIAL_BROKER: this.credentialBrokerPath,
      MOHIST_MANAGER_EXECUTION_ID: this.grant.executionId,
    }
  }

  /**
   * Pi's real Bash tool calls this operation. Generic commands inherit only
   * the non-secret broker locator; the broker keeps bearer values inside the
   * child process that performs the CLI request.
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
            onLine: (line) => {
              if (line.includes('manager_credential_expired') || line.includes('manager_epoch_changed'))
                this.authorizationInvalidated = true
              options.onData(Buffer.from(`${this.mask(line)}\n`, 'utf8'))
            },
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
    return this.authorizationInvalidated || this.expired()
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
    await this.terminateChildren()
    await new Promise<void>((resolve) => {
      const broker = this.broker
      const credentialBroker = this.credentialBroker
      this.broker = null
      this.credentialBroker = null
      // Destroy remaining sockets first so `close` cannot block on a
      // half-open connection whose response will never be written.
      for (const socket of this.sockets) socket.destroy()
      this.sockets.clear()
      let remaining = (broker ? 1 : 0) + (credentialBroker ? 1 : 0)
      if (remaining === 0) {
        resolve()
        return
      }
      const closed = () => {
        remaining -= 1
        if (remaining === 0) resolve()
      }
      broker?.close(closed)
      credentialBroker?.close(closed)
    }).catch(() => undefined)
    await rm(this.directory, { recursive: true, force: true }).catch(() => undefined)
  }

  private async terminateChildren(): Promise<void> {
    for (const child of this.children) {
      if (child.exitCode === null) child.kill('SIGTERM')
    }
    if (this.children.size === 0) return
    await this.awaitChildExit(this.terminationTimeoutMs)
    // `killed` only records that some signal was sent; a child that traps or
    // ignores SIGTERM still needs the unconditional escalation.
    for (const child of this.children) {
      if (child.exitCode === null) child.kill('SIGKILL')
    }
    await this.awaitChildExit(this.terminationTimeoutMs)
    this.children.clear()
  }

  private awaitChildExit(timeoutMs: number): Promise<void> {
    if ([...this.children].every((child) => child.exitCode !== null)) return Promise.resolve()
    return new Promise<void>((resolve) => {
      const settle = () => {
        clearTimeout(timer)
        resolve()
      }
      const timer = setTimeout(settle, timeoutMs)
      timer.unref?.()
      const check = () => {
        if ([...this.children].every((child) => child.exitCode !== null)) settle()
      }
      for (const child of this.children) child.once('close', check)
    })
  }

  private async writeLauncher(): Promise<void> {
    const launcher = this.launcherPath
    const source = `#!/usr/bin/env node
const net = require('node:net')
const broker = process.env.MOHIST_MANAGER_BROKER
if (!broker) process.exit(126)
const args = process.argv.slice(2)
const kind = args[0] === 'slack' && args[1] === 'message' && args[2] === 'send' ? 'reply' : 'management'
const socket = net.createConnection(broker)
let body = ''
socket.on('data', (chunk) => { body += chunk.toString() })
socket.on('error', () => process.exit(126))
socket.on('end', () => {
  let response
  try { response = JSON.parse(body) } catch { process.exit(126); return }
  if (!response || typeof response.exitCode !== 'number') { process.exit(126); return }
  if (typeof response.stdout === 'string') process.stdout.write(response.stdout)
  if (typeof response.stderr === 'string') process.stderr.write(response.stderr)
  process.exit(response.exitCode)
})
socket.end(JSON.stringify({ kind, args }))
`
    await writeFile(launcher, source, { encoding: 'utf8', mode: 0o700 })
    if (process.platform !== 'win32') await chmod(launcher, 0o700)
  }

  private async startBroker(): Promise<void> {
    if (process.platform !== 'linux') {
      throw new Error('Manager execution broker requires Linux Unix-socket peer authentication')
    }
    // allowHalfOpen keeps the server socket writable after the client's
    // request end, because the response is produced asynchronously once the
    // CLI child exits; every handling path still ends or destroys the socket.
    const broker = createServer({ allowHalfOpen: true }, (socket) => this.handleConnection(socket))
    await this.listen(broker, this.socketPath)
    this.broker = broker

    const credentialBroker = createServer({ allowHalfOpen: true }, (socket) => this.handleCredentialConnection(socket))
    await this.listen(credentialBroker, this.credentialBrokerPath)
    this.credentialBroker = credentialBroker
  }

  private async listen(server: Server, path: string): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      server.once('error', reject)
      server.listen(path, () => {
        server.removeListener('error', reject)
        resolve()
      })
    })
  }

  private handleConnection(socket: Socket): void {
    this.sockets.add(socket)
    socket.on('close', () => this.sockets.delete(socket))
    let body = ''
    socket.setEncoding('utf8')
    socket.on('data', (chunk) => {
      body += chunk
      if (body.length > 64 * 1024) socket.destroy()
    })
    socket.on('error', () => socket.destroy())
    socket.on('end', () => {
      void this.handleRequest(socket, body)
    })
  }

  private async handleRequest(socket: Socket, body: string): Promise<void> {
    if (!(await isManagerLauncherConnection(socket, this.launcherPath))) {
      socket.end('{}')
      return
    }

    let request: { kind?: unknown; args?: unknown; cwd?: unknown }
    try {
      request = JSON.parse(body) as { kind?: unknown; args?: unknown; cwd?: unknown }
    } catch {
      socket.end('{}')
      return
    }
    const verdict = this.admitRequest(request)
    if (verdict === null) {
      socket.end('{}')
      return
    }
    await this.executeCli(socket, verdict.kind, verdict.args)
  }

  /**
   * Confinement gate for every broker request, launcher or not. A request is
   * admitted only when its arguments resolve to a Manager capability, the
   * declared kind matches that capability, the execution is live, no other
   * credential-bearing child is running, and the kind still has request
   * budget. The caller-supplied working directory is never used.
   */
  private admitRequest(request: {
    kind?: unknown
    args?: unknown
    cwd?: unknown
  }): { kind: 'management' | 'reply'; args: string[] } | null {
    const kind = request.kind === 'management' || request.kind === 'reply' ? request.kind : null
    if (kind === null) return null
    if (!Array.isArray(request.args) || request.args.length > 128) return null
    if (request.args.some((arg) => typeof arg !== 'string' || arg.length === 0)) return null
    if (this.disposed || this.expired()) return null
    if (this.runningChildren > 0) return null
    if (this.usedRequests[kind] >= this.requestLimits[kind]) return null
    const args = request.args as string[]
    const capability = resolveManagerRequestCapability(args)
    const admittedKind =
      capability !== null ? managerRequestKind(capability) : isManagerUsageRequest(args) ? 'management' : null
    if (admittedKind !== kind) return null
    this.usedRequests[kind] += 1
    return { kind, args }
  }

  private handleCredentialConnection(socket: Socket): void {
    this.sockets.add(socket)
    socket.on('close', () => this.sockets.delete(socket))
    let body = ''
    socket.setEncoding('utf8')
    socket.on('data', (chunk) => {
      body += chunk
      if (body.length > 16 * 1024 * 1024) socket.destroy()
    })
    socket.on('error', () => socket.destroy())
    socket.on('end', () => {
      void this.handleCredentialRequest(socket, body)
    })
  }

  private async handleCredentialRequest(socket: Socket, body: string): Promise<void> {
    const child = [...this.children].find((candidate) => candidate.pid === this.activeCredentialChildPid)
    if (
      this.disposed ||
      this.activeCredentialChildPid === null ||
      !child ||
      !(await isManagerProcessConnection(socket, this.realMoPath, this.activeCredentialChildPid))
    ) {
      socket.end('{}')
      return
    }

    let request: ManagerCredentialRequest
    try {
      request = JSON.parse(body) as ManagerCredentialRequest
    } catch {
      socket.end('{}')
      return
    }
    let requestUrl: URL
    try {
      requestUrl = new URL(String(request.url))
    } catch {
      socket.end('{}')
      return
    }
    if (
      typeof request.url !== 'string' ||
      !/^https?:$/i.test(requestUrl.protocol) ||
      typeof request.method !== 'string' ||
      (request.headers && (typeof request.headers !== 'object' || Array.isArray(request.headers)))
    ) {
      socket.end('{}')
      return
    }
    const kind = this.activeCredentialKind
    if (!kind) {
      socket.end('{}')
      return
    }

    try {
      const headers = new Headers()
      for (const [name, value] of Object.entries(request.headers ?? {})) {
        if (typeof value !== 'string' || name.toLowerCase() === 'authorization') continue
        headers.set(name, value)
      }
      headers.set(
        'authorization',
        `Bearer ${kind === 'management' ? this.grant.managementCredential : this.grant.replyCredential}`,
      )
      const method = request.method.toUpperCase()
      const response = await fetch(requestUrl, {
        method,
        headers,
        body:
          request.bodyBase64 && method !== 'GET' && method !== 'HEAD'
            ? Buffer.from(request.bodyBase64, 'base64')
            : undefined,
        redirect: 'error',
      })
      const responseBody = Buffer.from(await response.arrayBuffer())
      socket.end(
        JSON.stringify({
          status: response.status,
          headers: Object.fromEntries(response.headers.entries()),
          bodyBase64: responseBody.toString('base64'),
        } satisfies ManagerCredentialResponse),
      )
    } catch {
      socket.end(JSON.stringify({ status: 502, headers: {}, bodyBase64: '' } satisfies ManagerCredentialResponse))
    }
  }

  private async executeCli(socket: Socket, kind: 'management' | 'reply', args: string[]): Promise<void> {
    const childEnvironment = { ...this.baseEnvironment }
    delete childEnvironment.MOHIST_MANAGER_BROKER
    delete childEnvironment.MOHIST_MANAGER_EXECUTION_ID
    delete childEnvironment.MOHIST_MANAGER_MANAGEMENT_TOKEN
    delete childEnvironment.MOHIST_MANAGER_REPLY_TOKEN
    delete childEnvironment.MOHIST_MANAGER_CREDENTIAL_BROKER
    childEnvironment.MOHIST_MANAGER_CREDENTIAL_BROKER = this.credentialBrokerPath

    const child = spawn(this.realMoPath, args, {
      cwd: this.frozenCwd,
      env: childEnvironment,
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    this.children.add(child)
    this.activeCredentialChildPid = child.pid ?? null
    this.activeCredentialKind = kind
    this.runningChildren += 1
    let stdout = ''
    let stderr = ''
    child.stdout.on('data', (chunk: Buffer) => {
      stdout += this.mask(chunk.toString('utf8'))
    })
    child.stderr.on('data', (chunk: Buffer) => {
      stderr += this.mask(chunk.toString('utf8'))
    })
    const clearChild = () => {
      this.children.delete(child)
      if (this.activeCredentialChildPid === child.pid) {
        this.activeCredentialChildPid = null
        this.activeCredentialKind = null
      }
      this.runningChildren -= 1
    }
    child.on('error', () => {
      clearChild()
      socket.end('{}')
    })
    child.on('close', (exitCode) => {
      clearChild()
      if (this.disposed) {
        socket.destroy()
        return
      }
      if (socket.destroyed) return
      socket.end(
        JSON.stringify({
          exitCode: typeof exitCode === 'number' ? exitCode : 1,
          stdout,
          stderr,
        }),
      )
    })
  }
}

type ManagerCredentialRequest = {
  method?: unknown
  url?: unknown
  headers?: Record<string, unknown>
  bodyBase64?: string
}

type ManagerCredentialResponse = {
  status: number
  headers: Record<string, string>
  bodyBase64: string
}

function findRealMoPath(managerDirectory: string, pathValue: string): string | null {
  const separator = process.platform === 'win32' ? ';' : ':'
  const executable = process.platform === 'win32' ? 'mo.exe' : 'mo'
  return (
    pathValue
      .split(separator)
      .filter((item) => item && join(item) !== managerDirectory)
      .map((item) => join(item, executable))
      .find((candidate) => existsSync(candidate)) ?? null
  )
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
