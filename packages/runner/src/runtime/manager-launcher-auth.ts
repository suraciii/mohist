import { execFile } from 'node:child_process'
import { existsSync } from 'node:fs'
import { readFile, readlink, realpath } from 'node:fs/promises'
import { promisify } from 'node:util'
import type { Socket } from 'node:net'
import { delimiter, join } from 'node:path'

const execFileAsync = promisify(execFile)
const MAX_SOCKET_TABLE_BYTES = 4 * 1024 * 1024

interface SocketHandle {
  readonly _handle?: { readonly fd?: unknown }
}

/**
 * A Unix socket's mode only authenticates the Unix user. On Linux, pair the
 * accepted socket with its kernel-reported peer and require that peer to be
 * the generated launcher process. The check fails closed when the platform,
 * socket handle, procfs view, or socket-inspection utility is unavailable.
 */
export async function isManagerLauncherConnection(socket: Socket, launcherPath: string): Promise<boolean> {
  if (process.platform !== 'linux') return false
  const fd = (socket as Socket & SocketHandle)._handle?.fd
  if (typeof fd !== 'number' || !Number.isInteger(fd) || fd < 0) return false

  const acceptedInode = await readSocketInode(`/proc/${process.pid}/fd/${fd}`)
  if (!acceptedInode) return false
  const peer = await findPeerProcess(acceptedInode)
  if (!peer) return false
  const peerSocket = await readSocketInode(`/proc/${peer.pid}/fd/${peer.fd}`)
  if (peerSocket !== peer.inode) return false

  const commandLine = await readProcessCommandLine(peer.pid)
  if (commandLine.length < 2) return false
  return await samePath(commandLine[1], launcherPath)
}

async function findPeerProcess(acceptedInode: string): Promise<{ pid: number; fd: number; inode: string } | null> {
  const ssPath = resolveSocketInspector()
  if (!ssPath) return null
  let output: string
  try {
    const result = await execFileAsync(ssPath, ['-xnp'], {
      encoding: 'utf8',
      maxBuffer: MAX_SOCKET_TABLE_BYTES,
    })
    output = result.stdout
  } catch {
    return null
  }

  for (const line of output.split('\n')) {
    const endpoints = line.match(/\*\s+(\d+)\s+\*\s+(\d+)\s+users:/)
    if (!endpoints || endpoints[2] !== acceptedInode) continue
    const peerInode = endpoints[1]
    for (const match of line.matchAll(/pid=(\d+),fd=(\d+)/g)) {
      const pid = Number(match[1])
      const fd = Number(match[2])
      if (!Number.isSafeInteger(pid) || !Number.isSafeInteger(fd)) continue
      if ((await readSocketInode(`/proc/${pid}/fd/${fd}`)) === peerInode) {
        return { pid, fd, inode: peerInode }
      }
    }
  }
  return null
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
