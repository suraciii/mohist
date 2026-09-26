import { chmod, mkdtemp, writeFile } from 'node:fs/promises'
import type { Socket } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { inspectManagerPeer, scanSocketInspectorRows } from '../../src/runtime/manager-launcher-auth.js'

// A row shape the scanner can accept. Its content does not matter to the scan
// outcome; it only selects which path the stub drives.
const acceptedRow = 'u_str ESTAB 0 0 * 4243 * 4242 users:(("node",pid=77,fd=9))'

async function writeInspector(body: string): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), 'mohist-launcher-auth-'))
  const inspector = join(root, 'ss-stub.mjs')
  await writeFile(inspector, `#!/usr/bin/env node\n${body}`, { encoding: 'utf8', mode: 0o700 })
  await chmod(inspector, 0o700)
  return inspector
}

/**
 * Writes one row and runs `after` once the row has left the process, so the
 * ending is ordered after the scan could have read the row.
 */
function afterWrite(row: string, after: string): string {
  return `process.stdout.write(${JSON.stringify(`${row}\n`)}, () => {\n${after}\n})\n`
}

// The cases below keep real inspector processes in the loop. They are
// supplements to the seam-driven lifecycle tests: they prove the production
// spawn path, while the exact endings stay pinned without process timing.
describe('Socket inspector row scanning', () => {
  it('accepts an accepted row from an inspector that ends cleanly', async () => {
    const inspector = await writeInspector(afterWrite(acceptedRow, '  process.exit(0)'))

    await expect(scanSocketInspectorRows(inspector, () => true)).resolves.toBeUndefined()
  })

  it('fails closed when the inspector exits non-zero after writing an accepted row', async () => {
    const inspector = await writeInspector(afterWrite(acceptedRow, '  process.exit(7)'))

    await expect(scanSocketInspectorRows(inspector, () => true)).rejects.toMatchObject({
      name: 'SocketInspectorError',
      code: 'inspector-exit-nonzero:7',
    })
  })

  it('fails closed when the inspector terminates itself after writing an accepted row', async () => {
    const inspector = await writeInspector(afterWrite(acceptedRow, "  process.kill(process.pid, 'SIGTERM')"))

    await expect(scanSocketInspectorRows(inspector, () => true)).rejects.toMatchObject({
      name: 'SocketInspectorError',
      code: 'inspector-terminated:SIGTERM',
    })
  })

  it('fails closed when the inspector is killed by another signal after writing an accepted row', async () => {
    const inspector = await writeInspector(afterWrite(acceptedRow, "  process.kill(process.pid, 'SIGKILL')"))

    await expect(scanSocketInspectorRows(inspector, () => true)).rejects.toMatchObject({
      name: 'SocketInspectorError',
      code: 'inspector-terminated:SIGKILL',
    })
  })

  it('fails closed when a signal ends the inspector without an accepted row', async () => {
    const inspector = await writeInspector(
      afterWrite(`u_str ESTAB 0 0 * 1 * 2 users:(("node",pid=5,fd=5))`, "  process.kill(process.pid, 'SIGTERM')"),
    )

    await expect(scanSocketInspectorRows(inspector, () => false)).rejects.toMatchObject({
      name: 'SocketInspectorError',
      code: 'inspector-terminated:SIGTERM',
    })
  })

  it('refuses the peer when the inspector fails after printing its row', async () => {
    await expectPeerRefusal(await writeInspector(afterWrite(acceptedRow, '  process.exit(7)')), {
      admitted: false,
      refusal: 'socket-table-unreadable',
      detail: 'inspector-exit-nonzero:7',
    })
  })

  it('refuses the peer when the inspector terminates itself after printing its row', async () => {
    await expectPeerRefusal(await writeInspector(afterWrite(acceptedRow, "  process.kill(process.pid, 'SIGTERM')")), {
      admitted: false,
      refusal: 'socket-table-unreadable',
      detail: 'inspector-terminated:SIGTERM',
    })
  })
})

async function expectPeerRefusal(inspector: string, expected: unknown): Promise<void> {
  const acceptedInode = '4242'
  const peerInode = '4243'
  const launcherPath = '/tmp/mohist-launcher-auth/mo'
  const verdict = await inspectManagerPeer({ _handle: { fd: 9 } } as unknown as Socket, launcherPath, undefined, {
    platform: 'linux',
    socketInode: async (path) =>
      path === `/proc/${process.pid}/fd/9` ? acceptedInode : path === '/proc/77/fd/9' ? peerInode : null,
    scanSocketTable: async (onRow) => {
      await scanSocketInspectorRows(inspector, onRow)
    },
    readCommandLine: async () => ['/usr/bin/node', launcherPath],
    samePath: async (left, right) => left === right,
  })

  expect(verdict).toEqual(expected)
}
