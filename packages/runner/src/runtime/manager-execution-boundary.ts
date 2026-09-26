import { createHash, randomUUID } from 'node:crypto'
import { existsSync } from 'node:fs'
import { spawn, type ChildProcess } from 'node:child_process'
import { chmod, copyFile, mkdir, rm, writeFile } from 'node:fs/promises'
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
import { inspectManagerPeer, type ManagerPeerVerdict } from './manager-launcher-auth.js'

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
  /** Injectable clock for expiry tests and deterministic lifecycle checks. */
  readonly now?: () => number
}

const DEFAULT_TERMINATION_TIMEOUT_MS = 5_000

// The launcher's conventional failure exit for an unreachable or refused
// Manager request. The broker answers with the same code so a refusal is
// distinguishable from transport loss on both sides.
const REFUSED_EXIT_CODE = 126

type ManagerRequestAdmission =
  | { readonly admitted: true; readonly kind: 'management' | 'reply'; readonly args: readonly string[] }
  | { readonly admitted: false; readonly reason: string }

// The broker uses Linux kernel peer information to admit only the generated
// launcher process. Capability confinement remains a second gate: bearer
// values remain in the Runner-side proxy, requests resolve through the Manager
// vocabulary, the working directory is frozen, and each kind has a bounded
// request budget. Ordinary credential environment values and credential-file
// fallbacks are removed from the runtime boundary. The Server independently
// enforces lease origin, route allowlist, and anchor validation for whatever
// those children send. A same-user process reading an arbitrary absolute path
// (for example the operator's admin-token outside the redirected HOME) or
// attaching to the loopback API with such a value remains outside what an
// in-process boundary can prevent; that residual requires OS-level sandboxing
// (a dedicated execution UID) and is deliberately not claimed here.
/**
 * One in-memory Manager process boundary. The grant is deliberately not part
 * of DispatchWorkItem and this class is never copied into a work report.
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
  private readonly now: () => number
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
  private expiryTimer: NodeJS.Timeout | null = null

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
    now: () => number,
  ) {
    this.grant = { ...grant }
    this.directory = directory
    this.socketPath = socketPath
    this.credentialBrokerPath = credentialBrokerPath
    this.launcherPath = join(directory, 'mo')
    this.baseEnvironment = baseEnvironment
    this.realMoPath = realMoPath
    this.frozenCwd = frozenCwd
    this.requestLimits = requestLimits
    this.terminationTimeoutMs = terminationTimeoutMs
    this.now = now
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
    const now = options.now ?? Date.now
    if (!Number.isFinite(Date.parse(grant.expiresAt)) || now() >= Date.parse(grant.expiresAt)) {
      throw new Error('Manager execution grant is expired or malformed')
    }
    const suffix = createHash('sha256')
      .update(`${grant.executionId}\n${grant.deploymentEpoch}\n${randomUUID()}`)
      .digest('hex')
      .slice(0, 32)
    const directory = join(runnerRoot, 'manager-executions', suffix)
    await mkdir(directory, { recursive: true, mode: 0o700 })
    // Linux limits Unix-socket pathnames to roughly 108 bytes. The Runner
    // root may be a long diagnostic path, so keep only these non-secret
    // endpoints under a short system path while private files stay rooted in
    // the Runner directory.
    const socketRoot = process.platform === 'linux' ? '/tmp' : directory
    const socketPath = join(socketRoot, `mohist-manager-${suffix}.sock`)
    const credentialBrokerPath = join(socketRoot, `mohist-manager-${suffix}-cred.sock`)
    const baseEnvironment: NodeJS.ProcessEnv = {
      ...process.env,
      MOHIST_MANAGER_MODE: '1',
    }
    delete baseEnvironment.MOHIST_MANAGER_MANAGEMENT_TOKEN
    delete baseEnvironment.MOHIST_MANAGER_REPLY_TOKEN
    delete baseEnvironment.MOHIST_MANAGER_CREDENTIAL_BROKER
    // Ordinary CLI credentials must never survive into the Manager runtime:
    // with them, a generic shell could bypass the capability catalog by
    // driving the real CLI or API directly. HOME is redirected into the
    // boundary so every `~/.mohist/*` credential-file fallback resolves
    // inside this empty directory instead (git identity is carried over so
    // workspace commits keep working).
    delete baseEnvironment.MOHIST_TOKEN
    delete baseEnvironment.MOHIST_ADMIN_TOKEN
    delete baseEnvironment.MOHIST_ADMIN_TOKEN_PATH
    const homeDirectory = join(directory, 'home')
    await mkdir(homeDirectory, { recursive: true, mode: 0o700 })
    const gitconfig = join(process.env.HOME ?? '', '.gitconfig')
    if (process.env.HOME && existsSync(gitconfig)) {
      await copyFile(gitconfig, join(homeDirectory, '.gitconfig')).catch(() => undefined)
    }
    baseEnvironment.HOME = homeDirectory
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
      options.now ?? Date.now,
    )
    boundary.scheduleExpiry()
    try {
      await boundary.writeLauncher()
      await boundary.startBroker()
      return boundary
    } catch (error) {
      await boundary.dispose().catch(() => undefined)
      throw error
    }
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
      MOHIST_MANAGER_LAUNCHER: this.launcherPath,
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
          this.shellEnvironment(options.env),
          {
            timeoutMs: options.timeout,
            isolatedEnvironment: true,
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
    if (this.disposed || this.authorizationInvalidated) return true
    if (this.now() < Date.parse(this.grant.expiresAt)) return false
    this.authorizationInvalidated = true
    void this.dispose()
    return true
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
    if (this.expiryTimer) clearTimeout(this.expiryTimer)
    this.expiryTimer = null
    // Revoke the bearer immediately. The masker remains alive until child
    // output has drained so an in-flight diagnostic can still be redacted.
    this.clearGrant()
    const runtime = this.isolatedOpenCodeRuntime
    this.isolatedOpenCodeRuntime = null
    await runtime?.shutdown().catch(() => undefined)
    await this.terminateChildren()
    // Do not retain bearer values in an otherwise-disposed boundary. The
    // masker itself is also cleared because it is part of the execution
    // object reachable from task-log and runtime observers until their final
    // callbacks settle.
    this.masker.clearSecrets()
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
    await rm(this.socketPath, { force: true }).catch(() => undefined)
    await rm(this.credentialBrokerPath, { force: true }).catch(() => undefined)
    await rm(this.directory, { recursive: true, force: true }).catch(() => undefined)
  }

  private scheduleExpiry(): void {
    const delay = Math.max(0, Date.parse(this.grant.expiresAt) - this.now())
    this.expiryTimer = setTimeout(() => {
      void this.dispose()
    }, delay)
    this.expiryTimer.unref?.()
  }

  private shellEnvironment(overrides?: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
    const environment = { ...this.baseEnvironment, ...(overrides ?? {}) }
    for (const name of [
      'HOME',
      'PATH',
      'MOHIST_MANAGER_MODE',
      'MOHIST_MANAGER_BROKER',
      'MOHIST_MANAGER_LAUNCHER',
      'MOHIST_MANAGER_CREDENTIAL_BROKER',
      'MOHIST_MANAGER_EXECUTION_ID',
      'MOHIST_TOKEN',
      'MOHIST_ADMIN_TOKEN',
      'MOHIST_ADMIN_TOKEN_PATH',
      'MOHIST_MANAGER_MANAGEMENT_TOKEN',
      'MOHIST_MANAGER_REPLY_TOKEN',
    ]) {
      delete environment[name]
    }
    // Reapply the fixed boundary values after caller environment overrides;
    // generic shell options cannot redirect HOME, PATH, or the broker to an
    // unrestricted CLI transport.
    return { ...environment, ...this.environment() }
  }

  private clearGrant(): void {
    const grant = this.grant as { managementCredential: string; replyCredential: string }
    grant.managementCredential = ''
    grant.replyCredential = ''
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
const fail = (reason) => {
  try { process.stderr.write('manager launcher: ' + reason + '\\n') } catch {}
  process.exit(126)
}
const broker = process.env.MOHIST_MANAGER_BROKER
if (!broker) fail('the broker locator is missing from the environment')
const args = process.argv.slice(2)
const kind = args[0] === 'slack' && args[1] === 'message' && args[2] === 'send' ? 'reply' : 'management'
const socket = net.createConnection(broker)
let body = ''
socket.on('data', (chunk) => { body += chunk.toString() })
socket.on('error', (error) => fail('the broker connection failed' + (error && error.code ? ' (' + error.code + ')' : '')))
socket.on('end', () => {
  if (body.length === 0) fail('the broker returned no response')
  let response
  try { response = JSON.parse(body) } catch { fail('the broker returned a malformed response') }
  if (!response || typeof response.exitCode !== 'number') fail('the broker response carried no exit code')
  if (typeof response.signal === 'string' && response.signal.length > 0)
    process.stderr.write('manager broker: the Manager command was terminated by ' + response.signal + '\\n')
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
    await rm(path, { force: true }).catch(() => undefined)
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
    const peer = await inspectManagerPeer(socket, this.launcherPath)
    if (!peer.admitted) {
      this.refuseRequest(socket, `peer identity not verified (${describeRefusal(peer)})`)
      return
    }

    let request: { kind?: unknown; args?: unknown; cwd?: unknown }
    try {
      request = JSON.parse(body) as { kind?: unknown; args?: unknown; cwd?: unknown }
    } catch {
      this.refuseRequest(socket, 'the request body is not valid JSON')
      return
    }
    const admission = this.admitRequest(request)
    if (!admission.admitted) {
      this.refuseRequest(socket, admission.reason)
      return
    }
    await this.executeCli(socket, admission.kind, admission.args)
  }

  /**
   * A refusal answers with the broker's exit-code convention and a
   * category-only reason. The Manager can tell a refusal from transport loss,
   * while the payload still carries no credentials, socket paths or process
   * identifiers.
   */
  private refuseRequest(socket: Socket, reason: string): void {
    if (socket.destroyed) return
    socket.end(
      JSON.stringify({
        exitCode: REFUSED_EXIT_CODE,
        stdout: '',
        stderr: `manager broker: ${reason}\n`,
      }),
    )
  }

  /**
   * Confinement gate for every broker request, launcher or not. A request is
   * admitted only when its arguments resolve to a Manager capability, the
   * declared kind matches that capability, the execution is live, no other
   * credential-bearing child is running, and the kind still has request
   * budget. The caller-supplied working directory is never used.
   */
  private admitRequest(request: { kind?: unknown; args?: unknown; cwd?: unknown }): ManagerRequestAdmission {
    const kind = request.kind === 'management' || request.kind === 'reply' ? request.kind : null
    if (kind === null) return { admitted: false, reason: 'the request kind is not a Manager request kind' }
    if (!Array.isArray(request.args) || request.args.length > 128) {
      return { admitted: false, reason: 'the request arguments are not a bounded string list' }
    }
    if (request.args.some((arg) => typeof arg !== 'string' || arg.length === 0)) {
      return { admitted: false, reason: 'the request arguments are not a bounded string list' }
    }
    if (this.disposed || this.expired()) return { admitted: false, reason: 'the execution is closed or expired' }
    if (this.runningChildren > 0) return { admitted: false, reason: 'another Manager request is in flight' }
    if (this.usedRequests[kind] >= this.requestLimits[kind]) {
      return { admitted: false, reason: `the ${kind} request budget is exhausted` }
    }
    const args = request.args as string[]
    const capability = resolveManagerRequestCapability(args)
    const admittedKind =
      capability !== null ? managerRequestKind(capability) : isManagerUsageRequest(args) ? 'management' : null
    if (admittedKind !== kind) {
      return { admitted: false, reason: 'the request is outside the Manager capability catalog' }
    }
    this.usedRequests[kind] += 1
    return { admitted: true, kind, args }
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
    if (this.expired() || this.activeCredentialChildPid === null || !child) {
      socket.end('{}')
      return
    }
    if (!(await inspectManagerPeer(socket, this.realMoPath, this.activeCredentialChildPid)).admitted) {
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

  private async executeCli(socket: Socket, kind: 'management' | 'reply', args: readonly string[]): Promise<void> {
    if (this.expired()) {
      this.refuseRequest(socket, 'the execution is closed or expired')
      return
    }
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
      stdout += chunk.toString('utf8')
    })
    child.stderr.on('data', (chunk: Buffer) => {
      stderr += chunk.toString('utf8')
    })
    let childCleared = false
    const clearChild = () => {
      if (childCleared) return
      childCleared = true
      this.children.delete(child)
      if (this.activeCredentialChildPid === child.pid) {
        this.activeCredentialChildPid = null
        this.activeCredentialKind = null
      }
      this.runningChildren -= 1
    }
    // A failed spawn emits `error` and then `close`. Exactly one response
    // settles the request; a second write would surface as a reset instead of
    // the diagnostic.
    let responded = false
    const respond = (payload: Record<string, unknown>) => {
      if (responded) return
      responded = true
      if (socket.destroyed) return
      socket.end(JSON.stringify(payload))
    }
    child.on('error', (error: Error & { readonly code?: string }) => {
      clearChild()
      respond({
        exitCode: REFUSED_EXIT_CODE,
        stdout: '',
        stderr: `manager broker: the Manager command could not be started${error.code ? ` (${error.code})` : ''}\n`,
      })
    })
    child.on('close', (exitCode, signal) => {
      clearChild()
      if (this.disposed) {
        socket.destroy()
        return
      }
      respond({
        exitCode: typeof exitCode === 'number' ? exitCode : 1,
        ...(signal ? { signal } : {}),
        stdout: this.mask(stdout),
        stderr: this.mask(stderr),
      })
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

function describeRefusal(verdict: ManagerPeerVerdict): string {
  const refusal = verdict.refusal ?? 'unknown'
  return verdict.detail ? `${refusal}:${verdict.detail}` : refusal
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
