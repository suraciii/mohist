import type { Socket } from 'node:net'
import { Readable } from 'node:stream'
import { describe, expect, it } from 'vitest'
import {
  SocketInspectorError,
  inspectManagerPeer,
  scanSocketInspectorRows,
  type ManagerPeerAuthDeps,
  type SocketInspectorEnd,
  type SocketInspectorRun,
} from '../src/runtime/manager-launcher-auth.js'

const matchedPid = 77
const peerInode = '4243'
const acceptedInode = '4242'
const launcherPath = '/tmp/mohist-manager-unit/mo'

// Row shape as `ss -xnp` prints it: the local inode belongs to the peer
// process, the peer inode is this process's accepted socket.
const matchingRow = `u_str ESTAB 0 0 * ${peerInode} * ${acceptedInode} users:(("node",pid=${matchedPid},fd=9))`

const acceptedFdPath = `/proc/${process.pid}/fd/9`
const peerFdPath = `/proc/${matchedPid}/fd/9`

const connectedSocket = { _handle: { fd: 9 } } as unknown as Socket

interface PeerDepsOptions {
  readonly platform?: NodeJS.Platform
  readonly rows?: string[]
  readonly inodes?: ReadonlyArray<readonly [string, string]>
  readonly socketInode?: ManagerPeerAuthDeps['socketInode']
  readonly scanError?: Error
  readonly readCommandLine?: ManagerPeerAuthDeps['readCommandLine']
}

function peerDeps(options: PeerDepsOptions = {}): ManagerPeerAuthDeps {
  const inodes: Record<string, string> = Object.fromEntries(
    options.inodes ?? [
      [acceptedFdPath, acceptedInode],
      [peerFdPath, peerInode],
    ],
  )
  return {
    platform: options.platform ?? 'linux',
    socketInode: options.socketInode ?? (async (path) => inodes[path] ?? null),
    scanSocketTable: async (onRow) => {
      if (options.scanError) throw options.scanError
      for (const row of options.rows ?? [matchingRow]) {
        if (await onRow(row)) return
      }
    },
    readCommandLine: options.readCommandLine ?? (async () => ['/usr/bin/node', launcherPath]),
    samePath: async (left, right) => left === right,
  }
}

describe('Manager peer authentication', () => {
  it('admits the launcher process that owns the peer socket', async () => {
    expect(await inspectManagerPeer(connectedSocket, launcherPath, undefined, peerDeps())).toEqual({ admitted: true })
  })

  it('refuses a peer on an unsupported platform', async () => {
    expect(
      await inspectManagerPeer(connectedSocket, launcherPath, undefined, peerDeps({ platform: 'darwin' })),
    ).toEqual({ admitted: false, refusal: 'unsupported-platform' })
  })

  it('refuses a socket whose handle exposes no file descriptor', async () => {
    expect(await inspectManagerPeer({} as unknown as Socket, launcherPath, undefined, peerDeps())).toEqual({
      admitted: false,
      refusal: 'socket-handle-unavailable',
    })
  })

  it('refuses when the accepted socket inode cannot be read', async () => {
    expect(
      await inspectManagerPeer(
        connectedSocket,
        launcherPath,
        undefined,
        peerDeps({ inodes: [[peerFdPath, peerInode]] }),
      ),
    ).toEqual({ admitted: false, refusal: 'socket-inode-unavailable' })
  })

  it('refuses when the peer cannot be found in the socket table', async () => {
    expect(
      await inspectManagerPeer(
        connectedSocket,
        launcherPath,
        undefined,
        peerDeps({
          rows: [`u_str ESTAB 0 0 * 1 * 2 users:(("node",pid=5,fd=5))`],
        }),
      ),
    ).toEqual({ admitted: false, refusal: 'peer-not-found' })
  })

  it('refuses with an inspector failure that carries no host data', async () => {
    const verdict = await inspectManagerPeer(
      connectedSocket,
      launcherPath,
      undefined,
      peerDeps({
        scanError: new SocketInspectorError('inspector-exit-nonzero:2'),
      }),
    )
    expect(verdict).toEqual({
      admitted: false,
      refusal: 'socket-table-unreadable',
      detail: 'inspector-exit-nonzero:2',
    })
    const serialized = JSON.stringify(verdict)
    expect(serialized).not.toContain('/proc')
    expect(serialized).not.toContain(String(matchedPid))
  })

  it('refuses a peer whose process id is not the expected child', async () => {
    expect(await inspectManagerPeer(connectedSocket, launcherPath, matchedPid + 1, peerDeps())).toEqual({
      admitted: false,
      refusal: 'peer-pid-mismatch',
    })
  })

  it('refuses when the candidate fd no longer holds the peer inode', async () => {
    let peerReads = 0
    const verdict = await inspectManagerPeer(
      connectedSocket,
      launcherPath,
      undefined,
      peerDeps({
        socketInode: async (path) => {
          if (path === acceptedFdPath) return acceptedInode
          if (path === peerFdPath) return peerReads++ === 0 ? peerInode : 'stale'
          return null
        },
      }),
    )
    expect(verdict).toEqual({ admitted: false, refusal: 'peer-inode-mismatch' })
  })

  it('refuses when the peer command line is unreadable', async () => {
    expect(
      await inspectManagerPeer(
        connectedSocket,
        launcherPath,
        undefined,
        peerDeps({
          readCommandLine: async () => [],
        }),
      ),
    ).toEqual({ admitted: false, refusal: 'command-line-unavailable' })
  })

  it('refuses a peer that is not running the expected executable', async () => {
    expect(
      await inspectManagerPeer(
        connectedSocket,
        launcherPath,
        undefined,
        peerDeps({
          readCommandLine: async () => ['/usr/bin/node', '/tmp/mohist-manager-unit/other'],
        }),
      ),
    ).toEqual({ admitted: false, refusal: 'command-line-mismatch' })
  })

  it('picks the candidate whose fd still holds the peer inode', async () => {
    const row = `u_str ESTAB 0 0 * ${peerInode} * ${acceptedInode} users:(("node",pid=11,fd=3),("node",pid=${matchedPid},fd=9))`
    const verdict = await inspectManagerPeer(
      connectedSocket,
      launcherPath,
      undefined,
      peerDeps({
        rows: [row],
        inodes: [
          [acceptedFdPath, acceptedInode],
          ['/proc/11/fd/3', 'other'],
          [peerFdPath, peerInode],
        ],
      }),
    )
    expect(verdict).toEqual({ admitted: true })
  })

  it('stops scanning at the first row that identifies the peer', async () => {
    const visited: string[] = []
    const verdict = await inspectManagerPeer(connectedSocket, launcherPath, undefined, {
      ...peerDeps(),
      scanSocketTable: async (onRow) => {
        for (const row of [matchingRow, `u_str ESTAB 0 0 * 9999 * 8888 users:(("node",pid=5,fd=5))`]) {
          visited.push(row)
          if (await onRow(row)) return
        }
      },
    })
    expect(verdict).toEqual({ admitted: true })
    expect(visited).toEqual([matchingRow])
  })
})

function inspectorRun(rows: readonly string[], end: Partial<SocketInspectorEnd> = {}): SocketInspectorRun {
  return {
    stdout: Readable.from(rows.map((row) => `${row}\n`)),
    outcome: Promise.resolve({ code: end.code ?? 0, signal: end.signal ?? null, error: end.error ?? null }),
  }
}

function failingStdout(): Readable {
  return new Readable({
    read() {
      this.destroy(Object.assign(new Error('broken pipe'), { code: 'EPIPE' }))
    },
  })
}

// The inspector lifecycle is driven through its spawn seam: the rules that
// decide whether rows count as evidence must not depend on real process
// timing, and a real inspector cannot be made to fail at an exact point.
describe('Socket inspector scan lifecycle', () => {
  it('accepts rows from an inspector that ends cleanly', async () => {
    const visited: string[] = []
    await expect(
      scanSocketInspectorRows(
        '/usr/bin/ss',
        async (row) => {
          visited.push(row)
          return false
        },
        () => inspectorRun([matchingRow, 'other-row']),
      ),
    ).resolves.toBeUndefined()
    expect(visited).toEqual([matchingRow, 'other-row'])
  })

  it('drains rows after the accepted one without deciding again', async () => {
    let calls = 0
    await expect(
      scanSocketInspectorRows(
        '/usr/bin/ss',
        async () => {
          calls += 1
          return true
        },
        () => inspectorRun([matchingRow, 'other-row']),
      ),
    ).resolves.toBeUndefined()
    expect(calls).toBe(1)
  })

  it('fails closed when a matching inspector exits non-zero', async () => {
    await expect(
      scanSocketInspectorRows(
        '/usr/bin/ss',
        async () => true,
        () => inspectorRun([matchingRow], { code: 7 }),
      ),
    ).rejects.toMatchObject({ code: 'inspector-exit-nonzero:7' })
  })

  it('fails closed when a matching inspector is terminated by a signal', async () => {
    for (const signal of ['SIGTERM', 'SIGKILL'] as const) {
      await expect(
        scanSocketInspectorRows(
          '/usr/bin/ss',
          async () => true,
          () => inspectorRun([matchingRow], { signal }),
        ),
      ).rejects.toMatchObject({ code: `inspector-terminated:${signal}` })
    }
  })

  it('fails closed when a signal ends the inspector without an accepted row', async () => {
    await expect(
      scanSocketInspectorRows(
        '/usr/bin/ss',
        async () => false,
        () => inspectorRun([matchingRow], { signal: 'SIGTERM' }),
      ),
    ).rejects.toMatchObject({ code: 'inspector-terminated:SIGTERM' })
  })

  it('classifies a spawn failure separately from a stream failure', async () => {
    await expect(
      scanSocketInspectorRows(
        '/usr/bin/ss',
        async () => false,
        () => inspectorRun([], { code: null, error: Object.assign(new Error('spawn failed'), { code: 'EACCES' }) }),
      ),
    ).rejects.toMatchObject({ code: 'inspector-spawn-failed:EACCES' })

    await expect(
      scanSocketInspectorRows(
        '/usr/bin/ss',
        async () => false,
        () => ({
          stdout: failingStdout(),
          outcome: Promise.resolve({ code: null, signal: null, error: null }),
        }),
      ),
    ).rejects.toMatchObject({ code: 'inspector-stream-failed:EPIPE' })
  })
})
