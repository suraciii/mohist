import { mkdtemp } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { createConnection } from 'node:net'
import { describe, expect, it } from 'vitest'
import { ManagerExecutionBoundary, type ManagerExecutionGrant } from './manager-execution-boundary.js'

const grant: ManagerExecutionGrant = {
  managementCredential: 'management-secret-012345678901234567890123456789',
  replyCredential: 'reply-secret-012345678901234567890123456789',
  executionId: 'manager:job-1:work-1:0',
  expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
  deploymentEpoch: 'mepoch_test',
}

describe('ManagerExecutionBoundary', () => {
  it('keeps bearer values out of the inherited environment and serves them only through the broker', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const boundary = await ManagerExecutionBoundary.create(grant, root)
    try {
      const environment = boundary.environment()
      expect(environment.MOHIST_MANAGER_MANAGEMENT_TOKEN).toBeUndefined()
      expect(environment.MOHIST_MANAGER_REPLY_TOKEN).toBeUndefined()
      expect(environment.MOHIST_MANAGER_BROKER).toContain('broker.sock')

      // A generic process that discovers the socket cannot authenticate as
      // the generated launcher and receives no bearer.
      expect(await requestBroker(environment.MOHIST_MANAGER_BROKER!, 'management')).toBeNull()

      expect(await requestBroker(environment.MOHIST_MANAGER_BROKER!, 'reply')).toBeNull()

      const output: Buffer[] = []
      await boundary.bashOperations().exec('env', root, {
        onData: (chunk) => output.push(chunk),
        signal: new AbortController().signal,
      })
      const genericEnvironment = Buffer.concat(output).toString('utf8')
      expect(genericEnvironment).not.toContain(grant.managementCredential)
      expect(genericEnvironment).not.toContain(grant.replyCredential)
    } finally {
      await boundary.dispose()
    }
  })

  it('redacts both credentials before output capture', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const boundary = await ManagerExecutionBoundary.create(grant, root)
    try {
      const output = boundary.mask(`management=${grant.managementCredential} reply=${grant.replyCredential}`)
      expect(output).toBe('management=*** reply=***')
    } finally {
      await boundary.dispose()
    }
  })
})

function requestBroker(path: string, kind: 'management' | 'reply'): Promise<string | null> {
  return new Promise((resolve, reject) => {
    const socket = createConnection(path)
    let body = ''
    socket.setEncoding('utf8')
    socket.on('data', (chunk) => {
      body += chunk
    })
    socket.on('error', reject)
    socket.on('end', () => {
      try {
        const parsed = JSON.parse(body) as { credential?: string }
        resolve(parsed.credential ?? null)
      } catch (error) {
        reject(error)
      }
    })
    socket.on('connect', () => {
      socket.end(JSON.stringify({ kind }))
    })
  })
}
