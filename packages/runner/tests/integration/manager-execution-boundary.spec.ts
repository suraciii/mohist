import { spawn } from 'node:child_process'
import { chmod, mkdir, mkdtemp, writeFile } from 'node:fs/promises'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { createConnection } from 'node:net'
import { describe, expect, it } from 'vitest'
import { ManagerExecutionBoundary, type ManagerExecutionGrant } from '../../src/runtime/manager-execution-boundary.js'
import {
  isManagerUsageRequest,
  managerRequestKind,
  resolveManagerRequestCapability,
} from '../../src/runtime/manager-capability-surface.js'

const grant: ManagerExecutionGrant = {
  managementCredential: 'management-secret-012345678901234567890123456789',
  replyCredential: 'reply-secret-012345678901234567890123456789',
  executionId: 'manager:job-1:work-1:0',
  expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
  deploymentEpoch: 'mepoch_test',
}

interface StubInvocation {
  args: string[]
  cwd: string
  managementToken: boolean
  replyToken: boolean
}

interface StubMo {
  readonly executable: string
  readonly marker: string
  readonly frozenWorkDir: string
  invocations(): StubInvocation[]
}

async function writeStubMo(root: string, holdSignal: boolean): Promise<StubMo> {
  const directory = join(root, 'stub-bin')
  const executable = join(directory, 'mo')
  const marker = join(root, 'stub-invocations.jsonl')
  const script = `#!/usr/bin/env node
const fs = require('node:fs')
const record = {
  args: process.argv.slice(2),
  cwd: process.cwd(),
  managementToken: process.env.MOHIST_MANAGER_MANAGEMENT_TOKEN === ${JSON.stringify(grant.managementCredential)},
  replyToken: process.env.MOHIST_MANAGER_REPLY_TOKEN === ${JSON.stringify(grant.replyCredential)},
}
fs.appendFileSync(${JSON.stringify(marker)}, JSON.stringify(record) + '\\n')
process.stdout.write('out ' + (process.env.MOHIST_MANAGER_MANAGEMENT_TOKEN ?? process.env.MOHIST_MANAGER_REPLY_TOKEN ?? 'none') + '\\n')
${holdSignal ? "process.on('SIGTERM', () => {})\nsetTimeout(() => process.exit(0), 60_000)\n" : 'process.exit(0)\n'}
`
  await mkdir(directory, { recursive: true })
  await writeFile(executable, script, { encoding: 'utf8', mode: 0o700 })
  await chmod(executable, 0o700)
  const frozenWorkDir = join(root, 'frozen-workdir')
  await mkdir(frozenWorkDir, { recursive: true })
  return {
    executable,
    marker,
    frozenWorkDir,
    invocations: () =>
      existsSync(marker)
        ? readFileSync(marker, 'utf8')
            .trim()
            .split('\n')
            .filter(Boolean)
            .map((line) => JSON.parse(line) as StubInvocation)
        : [],
  }
}

describe('ManagerExecutionBoundary', () => {
  it('keeps bearer values out of the inherited environment and refuses credential requests', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
      requestLimits: { reply: 5, management: 64 },
    })
    try {
      const environment = boundary.environment()
      expect(environment.MOHIST_MANAGER_MANAGEMENT_TOKEN).toBeUndefined()
      expect(environment.MOHIST_MANAGER_REPLY_TOKEN).toBeUndefined()
      expect(environment.MOHIST_MANAGER_BROKER).toContain('broker.sock')

      // The old forged credential request shape (no arguments) receives no
      // bearer and spawns nothing.
      const managementResponse = await requestBroker(environment.MOHIST_MANAGER_BROKER!, 'management')
      expect(managementResponse?.credential).toBeUndefined()
      expect(managementResponse?.exitCode).toBeUndefined()

      // A generic process cannot proxy an otherwise valid management request.
      const directManagement = await requestBroker(environment.MOHIST_MANAGER_BROKER!, 'management', [
        'slack',
        'status',
      ])
      const directReply = await requestBroker(environment.MOHIST_MANAGER_BROKER!, 'reply', [
        'slack',
        'message',
        'send',
        'attacker text',
      ])
      expect(directManagement?.exitCode).toBeUndefined()
      expect(directReply?.exitCode).toBeUndefined()
      expect(stub.invocations()).toHaveLength(0)

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

  it('admits only catalog commands with a matching kind, frozen cwd, and masked output', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
      requestLimits: { reply: 5, management: 64 },
    })
    try {
      const broker = boundary.environment().MOHIST_MANAGER_BROKER!

      // A management read from the catalog runs the child with only the
      // management bearer, in the frozen working directory.
      const status = await requestLauncher(broker, ['slack', 'status'])
      expect(status?.exitCode).toBe(0)
      const invocations = stub.invocations()
      expect(invocations).toHaveLength(1)
      expect(invocations[0].args).toEqual(['slack', 'status'])
      expect(invocations[0].cwd).toBe(stub.frozenWorkDir)
      expect(invocations[0].managementToken).toBe(true)
      expect(invocations[0].replyToken).toBe(false)
      // The stub echoes the bearer to stdout; the broker must mask it.
      expect(status?.stdout).toContain('***')
      expect(JSON.stringify(status)).not.toContain(grant.managementCredential)

      // The reply command is only valid under the reply kind.
      const mismatch = await requestBroker(broker, 'reply', ['slack', 'status'])
      expect(mismatch?.exitCode).toBeUndefined()
      expect(stub.invocations()).toHaveLength(1)

      // A forged reply target cannot escape the reply lease. The
      // capability-surface test covers the remaining invalid command shapes
      // without starting another process for each one.
      const refused = await requestLauncher(broker, ['slack', 'message', 'drop'])
      expect(refused.exitCode).toBe(126)
      expect(stub.invocations()).toHaveLength(1)

      // Usage and help requests mirror the CLI manager-mode admission and
      // run read-only under the management kind. The unit mirror test covers
      // the accepted flag variants; this verifies the broker path once.
      const usage = await requestLauncher(broker, ['--help'])
      expect(usage?.exitCode).toBe(0)
      const usageInvocations = stub.invocations()
      expect(usageInvocations).toHaveLength(2)
      expect(usageInvocations[1].managementToken).toBe(true)
      const usageReplyKind = await requestBroker(broker, 'reply', ['--help'])
      expect(usageReplyKind?.exitCode).toBeUndefined()
      expect(stub.invocations()).toHaveLength(2)

      const validReply = await requestLauncher(broker, ['slack', 'message', 'send', 'hello'])
      expect(validReply?.exitCode).toBe(0)
      const replyInvocations = stub.invocations()
      expect(replyInvocations).toHaveLength(3)
      expect(replyInvocations[2].replyToken).toBe(true)
      expect(replyInvocations[2].managementToken).toBe(false)
      expect(JSON.stringify(validReply)).not.toContain(grant.replyCredential)
    } finally {
      await boundary.dispose()
    }
  })

  it('enforces the per-kind request budget', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
      requestLimits: { reply: 1, management: 2 },
    })
    try {
      const broker = boundary.environment().MOHIST_MANAGER_BROKER!
      expect((await requestLauncher(broker, ['slack', 'status'])).exitCode).toBe(0)
      expect((await requestLauncher(broker, ['agent', 'list'])).exitCode).toBe(0)
      const exhausted = await requestLauncher(broker, ['slack', 'status'])
      expect(exhausted.exitCode).toBe(126)
      expect(stub.invocations()).toHaveLength(2)

      expect((await requestLauncher(broker, ['slack', 'message', 'send', 'hi'])).exitCode).toBe(0)
      const replyExhausted = await requestLauncher(broker, ['slack', 'message', 'send', 'again'])
      expect(replyExhausted.exitCode).toBe(126)
      expect(stub.invocations()).toHaveLength(3)
    } finally {
      await boundary.dispose()
    }
  })

  it('terminates token-bearing children within a bounded grace during disposal', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, true)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
      requestLimits: { reply: 5, management: 64 },
      terminationTimeoutMs: 150,
    })
    const broker = boundary.environment().MOHIST_MANAGER_BROKER!
    const pending = requestLauncher(broker, ['slack', 'status']).catch(() => null)
    await new Promise((resolve) => setTimeout(resolve, 120))
    expect(stub.invocations()).toHaveLength(1)
    const startedAt = Date.now()
    await boundary.dispose()
    expect(Date.now() - startedAt).toBeLessThan(10_000)
    await pending
    // The boundary directory is released only after the child tree is gone.
    expect(readdirSync(join(root, 'manager-executions'))).toHaveLength(0)
  })

  it('redacts both credentials before output capture', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: (await writeStubMo(root, false)).executable,
    })
    try {
      const output = boundary.mask(`management=${grant.managementCredential} reply=${grant.replyCredential}`)
      expect(output).toBe('management=*** reply=***')
    } finally {
      await boundary.dispose()
    }
  })
})

describe('manager capability surface mirror', () => {
  it('resolves the same vocabulary as the CLI manager-mode admission', () => {
    expect(resolveManagerRequestCapability(['slack', 'message', 'send', 'text'])).toBe('manager.reply')
    expect(resolveManagerRequestCapability(['slack', 'status'])).toBe('workspace.status')
    expect(resolveManagerRequestCapability(['slack', 'list'])).toBe('connection.list')
    expect(resolveManagerRequestCapability(['slack', 'list', '--workspace-team', 'T1'])).toBe('agent.list')
    expect(resolveManagerRequestCapability(['slack', 'view'])).toBe('connection.diagnostics')
    expect(resolveManagerRequestCapability(['slack', 'create'])).toBe('agent.create-or-mount')
    expect(resolveManagerRequestCapability(['slack', 'enable'])).toBe('connection.enable')
    expect(resolveManagerRequestCapability(['slack', 'disable'])).toBe('connection.disable')
    expect(resolveManagerRequestCapability(['slack', 'claim-owner'])).toBe('owner.claim')
    expect(resolveManagerRequestCapability(['slack', 'transfer-owner'])).toBe('owner.transfer')
    expect(resolveManagerRequestCapability(['slack', 'edit', '--access-policy', 'open'])).toBe(
      'connection.access-policy',
    )
    expect(resolveManagerRequestCapability(['slack', 'edit', '--bot-name', 'b'])).toBeNull()
    expect(resolveManagerRequestCapability(['agent', 'list'])).toBe('agent.list')
    expect(resolveManagerRequestCapability(['agent', 'view', 'a1'])).toBe('agent.view')
    expect(resolveManagerRequestCapability(['agent', 'create'])).toBe('agent.create-or-mount')
    expect(resolveManagerRequestCapability(['run', 'view'])).toBeNull()
    expect(resolveManagerRequestCapability([])).toBeNull()
    expect(managerRequestKind('manager.reply')).toBe('reply')
    expect(managerRequestKind('workspace.status')).toBe('management')
    expect(managerRequestKind('unknown.thing')).toBeNull()
    expect(managerRequestKind(null)).toBeNull()
  })

  it('admits usage and help requests exactly like Manager-mode mo', () => {
    expect(isManagerUsageRequest([])).toBe(true)
    expect(isManagerUsageRequest(['--manager'])).toBe(true)
    expect(isManagerUsageRequest(['--manager=true'])).toBe(true)
    expect(isManagerUsageRequest(['--help'])).toBe(true)
    expect(isManagerUsageRequest(['-h'])).toBe(true)
    expect(isManagerUsageRequest(['-?'])).toBe(true)
    expect(isManagerUsageRequest(['/?'])).toBe(true)
    expect(isManagerUsageRequest(['--help=expanded'])).toBe(true)
    expect(isManagerUsageRequest(['--manager', '--help'])).toBe(true)
    expect(isManagerUsageRequest(['slack', 'status'])).toBe(false)
    expect(isManagerUsageRequest(['run', 'view', '--help'])).toBe(true)
  })
})

function requestLauncher(
  brokerPath: string,
  args: string[],
): Promise<{ exitCode: number; stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    const child = spawn(join(dirname(brokerPath), 'mo'), args, {
      env: { ...process.env, MOHIST_MANAGER_BROKER: brokerPath },
      stdio: ['ignore', 'pipe', 'pipe'],
    })
    let stdout = ''
    let stderr = ''
    child.stdout.on('data', (chunk: Buffer) => {
      stdout += chunk.toString('utf8')
    })
    child.stderr.on('data', (chunk: Buffer) => {
      stderr += chunk.toString('utf8')
    })
    child.once('error', reject)
    child.once('close', (exitCode) =>
      resolve({ exitCode: typeof exitCode === 'number' ? exitCode : 126, stdout, stderr }),
    )
  })
}

function requestBroker(
  path: string,
  kind: 'management' | 'reply',
  args?: string[],
  cwd?: string,
): Promise<{ credential?: string; exitCode?: number; stdout?: string; stderr?: string } | null> {
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
        const parsed =
          body.length === 0
            ? null
            : (JSON.parse(body) as { credential?: string; exitCode?: number; stdout?: string; stderr?: string })
        resolve(parsed)
      } catch (error) {
        reject(error)
      }
    })
    // Disposal destroys in-flight sockets instead of writing a response.
    socket.on('close', () => {
      if (body.length === 0) resolve(null)
    })
    socket.on('connect', () => {
      socket.end(JSON.stringify({ kind, ...(args === undefined ? {} : { args }), ...(cwd ? { cwd } : {}) }))
    })
  })
}
