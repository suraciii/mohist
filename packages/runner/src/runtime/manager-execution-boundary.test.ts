import { chmod, mkdtemp, writeFile } from 'node:fs/promises'
import { spawn } from 'node:child_process'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
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

      const bin = await mkdtemp(join(root, 'real-mo-'))
      const realMo = join(bin, 'mo')
      await writeFile(realMo, '#!/bin/sh\nprintf "%s" "$MOHIST_MANAGER_REPLY_TOKEN"\n', { encoding: 'utf8', mode: 0o700 })
      await chmod(realMo, 0o700)
      const launcherDirectory = environment.PATH!.split(':')[0]
      const launcher = join(launcherDirectory, 'mo')
      const reply = await runLauncher(launcher, ['slack', 'message', 'send'], {
        ...environment,
        PATH: `${environment.PATH!.split(':')[0]}:${bin}:${dirname(process.execPath)}`,
      })
      expect(reply).toBe(grant.replyCredential)

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

function runLauncher(
  launcher: string,
  args: readonly string[],
  env: NodeJS.ProcessEnv,
): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn(launcher, [...args], { env, stdio: ['ignore', 'pipe', 'pipe'] })
    const output: Buffer[] = []
    const errors: Buffer[] = []
    child.stdout.on('data', (chunk) => output.push(Buffer.from(chunk)))
    child.stderr.on('data', (chunk) => errors.push(Buffer.from(chunk)))
    child.on('error', reject)
    child.on('exit', (code) => {
      if (code !== 0) {
        reject(new Error(`launcher exited ${code}: ${Buffer.concat(errors).toString('utf8')}`))
        return
      }
      resolve(Buffer.concat(output).toString('utf8'))
    })
  })
}
