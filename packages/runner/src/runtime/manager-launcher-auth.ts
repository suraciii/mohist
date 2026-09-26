import { spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { readFile, readlink, realpath } from 'node:fs/promises'
import { createInterface } from 'node:readline'
import type { Socket } from 'node:net'
import { delimiter, join } from 'node:path'

interface SocketHandle {
  readonly _handle?: { readonly fd?: unknown }
}

/**
 * Categories that stay useful for diagnostics while carrying no credentials,
 * no process identifiers and no socket data.
 */
export type ManagerPeerRefusal =
  | 'unsupported-platform'
  | 'socket-handle-unavailable'
  | 'socket-inode-unavailable'
  | 'socket-table-unreadable'
  | 'peer-not-found'
  | 'peer-pid-mismatch'
  | 'peer-inode-mismatch'
  | 'command-line-unavailable'
  | 'command-line-mismatch'

export interface ManagerPeerVerdict {
  readonly admitted: boolean
  readonly refusal?: ManagerPeerRefusal
  /** Underlying failure code for `socket-table-unreadable`, never host data. */
  readonly detail?: string
}

export interface ManagerPeerAuthDeps {
  readonly platform: NodeJS.Platform
  readonly socketInode: (path: string) => Promise<string | null>
  /** Streams socket-table rows and stops early when `onRow` returns true. */
  readonly scanSocketTable: (onRow: (row: string) => boolean | Promise<boolean>) => Promise<void>
  readonly readCommandLine: (pid: number) => Promise<string[]>
  readonly samePath: (left: string, right: string) => Promise<boolean>
}

export class SocketInspectorError extends Error {
  readonly code: string

  constructor(code: string) {
    super(code)
    this.name = 'SocketInspectorError'
    this.code = code
  }
}

/**
 * Authenticates a broker peer by its live process identity. The optional PID
 * binds the connection to the exact child created for one Manager request;
 * this prevents another same-user process from reusing the non-secret socket
 * locator to obtain a credential-bearing proxy.
 *
 * A Unix socket's mode only authenticates the Unix user. On Linux, pair the
 * accepted socket with its kernel-reported peer and require that peer to be
 * the generated launcher process. The check fails closed when the platform,
 * socket handle, procfs view, or socket-inspection utility is unavailable, and
 * every refusal names the stage that failed so a legitimate launcher never
 * fails as an unexplained silent exit.
 */
export async function inspectManagerPeer(
  socket: Socket,
  executablePath: string,
  expectedPid?: number,
  deps: ManagerPeerAuthDeps = defaultManagerPeerAuthDeps,
): Promise<ManagerPeerVerdict> {
  if (deps.platform !== 'linux') return { admitted: false, refusal: 'unsupported-platform' }
  const fd = (socket as Socket & SocketHandle)._handle?.fd
  if (typeof fd !== 'number' || !Number.isInteger(fd) || fd < 0) {
    return { admitted: false, refusal: 'socket-handle-unavailable' }
  }

  const acceptedInode = await deps.socketInode(`/proc/${process.pid}/fd/${fd}`)
  if (!acceptedInode) return { admitted: false, refusal: 'socket-inode-unavailable' }

  const found: { peer: { readonly pid: number; readonly fd: number; readonly inode: string } | null } = { peer: null }
  try {
    await deps.scanSocketTable(async (row) => {
      const endpoints = /\*\s+(\d+)\s+\*\s+(\d+)\s+users:/.exec(row)
      if (!endpoints || endpoints[2] !== acceptedInode) return false
      const peerInode = endpoints[1]
      for (const match of row.matchAll(/pid=(\d+),fd=(\d+)/g)) {
        const pid = Number(match[1])
        const candidateFd = Number(match[2])
        if (!Number.isSafeInteger(pid) || !Number.isSafeInteger(candidateFd)) continue
        if ((await deps.socketInode(`/proc/${pid}/fd/${candidateFd}`)) === peerInode) {
          found.peer = { pid, fd: candidateFd, inode: peerInode }
          return true
        }
      }
      return false
    })
  } catch (error) {
    return { admitted: false, refusal: 'socket-table-unreadable', detail: failureCode(error) }
  }
  const peer = found.peer
  if (!peer) return { admitted: false, refusal: 'peer-not-found' }
  if (expectedPid !== undefined && peer.pid !== expectedPid) return { admitted: false, refusal: 'peer-pid-mismatch' }
  if ((await deps.socketInode(`/proc/${peer.pid}/fd/${peer.fd}`)) !== peer.inode) {
    return { admitted: false, refusal: 'peer-inode-mismatch' }
  }

  const commandLine = await deps.readCommandLine(peer.pid)
  if (commandLine.length === 0) return { admitted: false, refusal: 'command-line-unavailable' }
  for (const argument of commandLine) {
    if (await deps.samePath(argument, executablePath)) return { admitted: true }
  }
  return { admitted: false, refusal: 'command-line-mismatch' }
}

export const defaultManagerPeerAuthDeps: ManagerPeerAuthDeps = {
  platform: process.platform,
  socketInode: readSocketInode,
  scanSocketTable: async (onRow) => {
    const inspector = resolveSocketInspector()
    if (!inspector) throw new SocketInspectorError('inspector-unavailable')
    await scanSocketInspectorRows(inspector, onRow)
  },
  readCommandLine: readProcessCommandLine,
  samePath,
}

/**
 * Streams the socket inspector's rows and stops at the first accepted row. A
 * large host socket table must cost time, not authentication: buffering the
 * whole table against a fixed byte cap refused legitimate launchers as soon as
 * a busy host crossed that cap, while leaving no trace of the cause.
 */
export async function scanSocketInspectorRows(
  inspectorPath: string,
  onRow: (row: string) => boolean | Promise<boolean>,
): Promise<void> {
  const child = spawn(inspectorPath, ['-xnp'], { stdio: ['ignore', 'pipe', 'ignore'] })
  const outcome = new Promise<{
    readonly code: number | null
    readonly signal: NodeJS.Signals | null
    readonly error: Error | null
  }>((resolve) => {
    let settled = false
    const settle = (code: number | null, signal: NodeJS.Signals | null, error: Error | null) => {
      if (settled) return
      settled = true
      resolve({ code, signal, error })
    }
    child.once('error', (error: Error) => settle(null, null, error))
    child.once('exit', (code: number | null, signal: NodeJS.Signals | null) => settle(code, signal, null))
  })

  let matched = false
  try {
    for await (const row of createInterface({ input: child.stdout, crlfDelay: Number.POSITIVE_INFINITY })) {
      if (await onRow(row)) {
        matched = true
        child.kill('SIGTERM')
        break
      }
    }
  } catch (error) {
    const spawnFailure = (await outcome).error
    if (spawnFailure) throw new SocketInspectorError(`inspector-spawn-failed:${failureCode(spawnFailure)}`)
    throw new SocketInspectorError(`inspector-stream-failed:${failureCode(error)}`)
  }

  const { code, signal, error } = await outcome
  if (error) throw new SocketInspectorError(`inspector-spawn-failed:${failureCode(error)}`)
  // Only this scan's own early stop may end a matched run: the inspector died
  // by the SIGTERM sent after it wrote a matching row. That attribution needs
  // the signal, not just the exit code, because a child that had already
  // failed on its own reports its own outcome here. Any other ending — a
  // non-zero exit, or death by another signal even after a match — means the
  // table is incomplete or was produced by a failing process, so its rows are
  // not authentication evidence and the scan fails closed.
  if (matched && signal === 'SIGTERM') return
  if (signal !== null) throw new SocketInspectorError(`inspector-terminated:${signal}`)
  if (code !== 0) throw new SocketInspectorError(`inspector-exit-nonzero:${code}`)
}

function failureCode(error: unknown): string {
  if (error instanceof SocketInspectorError) return error.code
  if (error && typeof error === 'object' && 'code' in error) return String((error as { code: unknown }).code)
  return error instanceof Error ? error.name : 'unknown'
}

function resolveSocketInspector(): string | null {
  const candidates = ['/usr/bin/ss', '/usr/sbin/ss', '/bin/ss']
  const pathValue = process.env.PATH ?? ''
  for (const directory of [...candidates, ...pathValue.split(delimiter).filter(Boolean)]) {
    const candidate = directory.endsWith('/ss') ? directory : join(directory, 'ss')
    if (existsSync(candidate)) return candidate
  }
  return null
}

async function readSocketInode(path: string): Promise<string | null> {
  try {
    const link = await readlink(path)
    return /^socket:\[(\d+)\]$/.exec(link)?.[1] ?? null
  } catch {
    return null
  }
}

async function readProcessCommandLine(pid: number): Promise<string[]> {
  try {
    return (await readFile(`/proc/${pid}/cmdline`)).toString('utf8').split('\0').filter(Boolean)
  } catch {
    return []
  }
}

async function samePath(left: string, right: string): Promise<boolean> {
  try {
    return (await realpath(left)) === (await realpath(right))
  } catch {
    return false
  }
}
