import { spawn } from 'node:child_process'
import { chmod, mkdir, mkdtemp, writeFile } from 'node:fs/promises'
import { existsSync, readFileSync, readdirSync, watch } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { createConnection, type Socket } from 'node:net'
import { createServer as createHttpServer, type Server as HttpServer } from 'node:http'
import { describe, expect, it } from 'vitest'
import { ManagerExecutionBoundary, type ManagerExecutionGrant } from '../../src/runtime/manager-execution-boundary.js'
import { inspectManagerPeer, scanSocketInspectorRows } from '../../src/runtime/manager-launcher-auth.js'
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
  credentialBroker: boolean
  pid: number
}

interface StubMo {
  readonly executable: string
  readonly marker: string
  readonly frozenWorkDir: string
  invocations(): StubInvocation[]
}

interface StubInvocationObserver {
  waitFor(launcher: Promise<unknown>): Promise<StubInvocation>
  close(): void
}

function observeInvocation(stub: StubMo): StubInvocationObserver {
  let watcher: ReturnType<typeof watch> | undefined
  let settled = false
  let resolveMarker!: (invocation: StubInvocation) => void
  let rejectMarker!: (error: Error) => void
  const marker = new Promise<StubInvocation>((resolve, reject) => {
    resolveMarker = resolve
    rejectMarker = reject
  })
  const close = () => {
    watcher?.close()
    watcher = undefined
  }
  const observe = () => {
    const invocation = stub.invocations()[0]
    if (!invocation || settled) return
    settled = true
    close()
    resolveMarker(invocation)
  }

  observe()
  if (!settled) {
    watcher = watch(dirname(stub.marker), observe)
    watcher.once('error', (error) => {
      if (settled) return
      settled = true
      close()
      rejectMarker(error)
    })
    observe()
  }

  return {
    waitFor(launcher) {
      const launcherTerminal = launcher.then(
        () => {
          close()
          throw new Error('launcher completed before the stub invocation marker was observed')
        },
        (error: unknown) => {
          close()
          throw new Error('launcher rejected before the stub invocation marker was observed', { cause: error })
        },
      )
      return Promise.race([marker, launcherTerminal])
    },
    close,
  }
}

async function writeStubMo(root: string, holdSignal: boolean, proxyUrl: string | null = null): Promise<StubMo> {
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
  credentialBroker: typeof process.env.MOHIST_MANAGER_CREDENTIAL_BROKER === 'string',
  pid: process.pid,
}
fs.appendFileSync(${JSON.stringify(marker)}, JSON.stringify(record) + '\\n')
${
  proxyUrl
    ? `const net = require('node:net')
const proxy = net.createConnection(process.env.MOHIST_MANAGER_CREDENTIAL_BROKER)
let proxyBody = ''
proxy.on('data', (chunk) => { proxyBody += chunk.toString() })
proxy.on('error', () => process.exit(1))
proxy.on('end', () => {
  const response = JSON.parse(proxyBody)
  process.stdout.write('proxy ' + response.status + '\\n')
  process.exit(response.status === 200 ? 0 : 1)
})
proxy.end(JSON.stringify({ method: 'GET', url: ${JSON.stringify(proxyUrl)}, headers: {} }))
`
    : "process.stdout.write('out ' + (process.env.MOHIST_MANAGER_MANAGEMENT_TOKEN ?? process.env.MOHIST_MANAGER_REPLY_TOKEN ?? 'none') + '\\n')"
}
${holdSignal ? "process.on('SIGTERM', () => {})\nsetTimeout(() => process.exit(0), 60_000)\n" : proxyUrl ? '' : 'process.exit(0)\n'}
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

describe.sequential('ManagerExecutionBoundary', () => {
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
      expect(environment.MOHIST_MANAGER_BROKER).toContain('mohist-manager-')

      // The old forged credential request shape (no arguments) receives no
      // bearer and spawns nothing; a non-launcher peer is refused with a
      // category-only reason that transport loss cannot imitate.
      const forged = await exchangeWithBroker(environment.MOHIST_MANAGER_BROKER!, 'management')
      expect(forged.socketError).toBeNull()
      const forgedResponse = parseBrokerBody(forged.body)
      expect(forgedResponse.credential).toBeUndefined()
      expect(forgedResponse.exitCode).toBe(126)
      expect(forgedResponse.stderr).toContain('peer identity not verified')

      // A generic process cannot proxy an otherwise valid management request.
      const directManagement = parseBrokerBody(
        (await exchangeWithBroker(environment.MOHIST_MANAGER_BROKER!, 'management', ['slack', 'status'])).body,
      )
      const directReply = parseBrokerBody(
        (
          await exchangeWithBroker(environment.MOHIST_MANAGER_BROKER!, 'reply', [
            'slack',
            'message',
            'send',
            'attacker text',
          ])
        ).body,
      )
      expect(directManagement.credential).toBeUndefined()
      expect(directManagement.exitCode).toBe(126)
      expect(directManagement.stderr).toContain('peer identity not verified')
      expect(directReply.credential).toBeUndefined()
      expect(directReply.exitCode).toBe(126)
      expect(directReply.stderr).toContain('peer identity not verified')
      expect(JSON.stringify(directManagement)).not.toContain(grant.managementCredential)
      expect(JSON.stringify(directReply)).not.toContain(grant.replyCredential)
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

  it('removes ordinary CLI credentials and credential-file fallbacks from the runtime', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const originalHome = process.env.HOME
    const fakeHome = join(root, 'operator-home')
    const credentialFileToken = 'a'.repeat(48)
    await mkdir(join(fakeHome, '.mohist'), { recursive: true })
    await writeFile(join(fakeHome, '.mohist', 'admin-token'), `${credentialFileToken}\n`, 'utf8')
    await writeFile(
      join(fakeHome, '.gitconfig'),
      '[user]\n\tname = Operator\n\temail = operator@example.test\n',
      'utf8',
    )
    process.env.HOME = fakeHome
    process.env.MOHIST_ADMIN_TOKEN = `b${'b'.repeat(47)}`
    process.env.MOHIST_TOKEN = `c${'c'.repeat(47)}`
    let boundary: ManagerExecutionBoundary | null = null
    try {
      boundary = await ManagerExecutionBoundary.create(grant, root, {
        moExecutable: stub.executable,
        workDir: stub.frozenWorkDir,
        requestLimits: { reply: 5, management: 64 },
      })
      const environment = boundary.environment()
      expect(environment.MOHIST_ADMIN_TOKEN).toBeUndefined()
      expect(environment.MOHIST_TOKEN).toBeUndefined()
      expect(environment.MOHIST_ADMIN_TOKEN_PATH).toBeUndefined()
      expect(environment.HOME).toContain('manager-executions')
      expect(environment.HOME).not.toBe(fakeHome)

      // The operator's credential file must not resolve from the redirected
      // HOME, while the carried-over git identity keeps workspace commits
      // working.
      const output: Buffer[] = []
      await boundary
        .bashOperations()
        .exec(
          'cat "$HOME/.mohist/admin-token"; test ! -f "$HOME/.mohist/admin-token"; test -f "$HOME/.gitconfig"',
          root,
          { onData: (chunk) => output.push(chunk), signal: new AbortController().signal },
        )
      expect(Buffer.concat(output).toString('utf8')).not.toContain(credentialFileToken)

      const genericEnv: Buffer[] = []
      await boundary.bashOperations().exec('env', root, {
        onData: (chunk) => genericEnv.push(chunk),
        signal: new AbortController().signal,
      })
      const seen = Buffer.concat(genericEnv).toString('utf8')
      expect(seen).not.toContain(process.env.MOHIST_ADMIN_TOKEN!)
      expect(seen).not.toContain(process.env.MOHIST_TOKEN!)
      expect(seen).not.toContain('admin-token')
    } finally {
      boundary?.dispose().catch(() => undefined)
      process.env.HOME = originalHome
      delete process.env.MOHIST_ADMIN_TOKEN
      delete process.env.MOHIST_TOKEN
    }
  })

  it('injects the bearer only inside the Runner-side request proxy', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    let targetServer: HttpServer | null = null
    const authorization = new Promise<string | undefined>((resolve) => {
      targetServer = createHttpServer((request, response) => {
        resolve(request.headers.authorization)
        response.writeHead(200, { 'content-type': 'text/plain' })
        response.end('ok')
      })
    })
    await new Promise<void>((resolve) => targetServer!.listen(0, '127.0.0.1', resolve))
    const address = targetServer!.address()
    if (!address || typeof address === 'string') throw new Error('credential proxy test server did not bind')
    const stub = await writeStubMo(root, false, `http://127.0.0.1:${address.port}/status`)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
    })
    try {
      const result = await requestLauncher(
        boundary.environment().MOHIST_MANAGER_BROKER!,
        ['slack', 'status'],
        boundary.environment().MOHIST_MANAGER_LAUNCHER!,
      )
      expect(result.exitCode).toBe(0)
      expect(result.stdout).toContain('proxy 200')
      expect(await authorization).toBe(`Bearer ${grant.managementCredential}`)
      expect(stub.invocations()[0].managementToken).toBe(false)
      expect(stub.invocations()[0].credentialBroker).toBe(true)
    } finally {
      await boundary.dispose()
      await new Promise<void>((resolve) => targetServer!.close(() => resolve()))
    }
  })

  it('admits a catalog management command in the frozen cwd with masked output', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
      requestLimits: { reply: 5, management: 64 },
    })
    try {
      const status = await requestLauncher(
        boundary.environment().MOHIST_MANAGER_BROKER!,
        ['slack', 'status'],
        boundary.environment().MOHIST_MANAGER_LAUNCHER!,
      )
      expect(status.exitCode).toBe(0)
      const invocations = stub.invocations()
      expect(invocations).toHaveLength(1)
      expect(invocations[0].args).toEqual(['slack', 'status'])
      expect(invocations[0].cwd).toBe(stub.frozenWorkDir)
      expect(invocations[0].managementToken).toBe(false)
      expect(invocations[0].replyToken).toBe(false)
      expect(invocations[0].credentialBroker).toBe(true)
      // The child has no bearer to echo or expose.
      expect(status.stdout).toContain('none')
      expect(JSON.stringify(status)).not.toContain(grant.managementCredential)
    } finally {
      await boundary.dispose()
    }
  })

  it('rejects a catalog command declared under the wrong request kind', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
    })
    try {
      // A direct connection from a process that is not the generated launcher
      // never reaches the request-kind gate: the peer check refuses it, and
      // the refusal is distinguishable from a transport failure.
      const mismatch = await exchangeWithBroker(boundary.environment().MOHIST_MANAGER_BROKER!, 'reply', [
        'slack',
        'status',
      ])
      const mismatchResponse = parseBrokerBody(mismatch.body)
      expect(mismatchResponse.exitCode).toBe(126)
      expect(mismatchResponse.stderr).toContain('peer identity not verified')
      expect(stub.invocations()).toHaveLength(0)
    } finally {
      await boundary.dispose()
    }
  })

  it('refuses commands outside the Manager capability catalog without launching the CLI', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
    })
    try {
      // A forged reply target cannot escape the reply lease. The
      // capability-surface test covers the remaining invalid command shapes
      // without starting another process for each one.
      const refused = await requestLauncher(
        boundary.environment().MOHIST_MANAGER_BROKER!,
        ['slack', 'message', 'drop'],
        boundary.environment().MOHIST_MANAGER_LAUNCHER!,
      )
      expect(refused.exitCode).toBe(126)
      expect(refused.signal).toBeNull()
      expect(refused.stderr).toContain('outside the Manager capability catalog')
      expect(stub.invocations()).toHaveLength(0)
    } finally {
      await boundary.dispose()
    }
  })

  it('admits help only under the management request kind', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
    })
    try {
      const broker = boundary.environment().MOHIST_MANAGER_BROKER!
      // The unit mirror test covers the accepted flag variants; this verifies
      // the broker path once.
      const usage = await requestLauncher(broker, ['--help'], boundary.environment().MOHIST_MANAGER_LAUNCHER!)
      expect(usage.exitCode).toBe(0)
      const usageInvocations = stub.invocations()
      expect(usageInvocations).toHaveLength(1)
      expect(usageInvocations[0].managementToken).toBe(false)
      expect(usageInvocations[0].credentialBroker).toBe(true)

      const usageReplyKind = parseBrokerBody((await exchangeWithBroker(broker, 'reply', ['--help'])).body)
      expect(usageReplyKind.exitCode).toBe(126)
      expect(usageReplyKind.stderr).toContain('peer identity not verified')
      expect(stub.invocations()).toHaveLength(1)
    } finally {
      await boundary.dispose()
    }
  })

  it('admits a reply command without exposing the reply bearer', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
    })
    try {
      const validReply = await requestLauncher(
        boundary.environment().MOHIST_MANAGER_BROKER!,
        ['slack', 'message', 'send', 'hello'],
        boundary.environment().MOHIST_MANAGER_LAUNCHER!,
      )
      expect(validReply.exitCode).toBe(0)
      const replyInvocations = stub.invocations()
      expect(replyInvocations).toHaveLength(1)
      expect(replyInvocations[0].replyToken).toBe(false)
      expect(replyInvocations[0].managementToken).toBe(false)
      expect(replyInvocations[0].credentialBroker).toBe(true)
      expect(JSON.stringify(validReply)).not.toContain(grant.replyCredential)
    } finally {
      await boundary.dispose()
    }
  })

  it('enforces the management request budget', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
      requestLimits: { reply: 5, management: 2 },
    })
    try {
      const broker = boundary.environment().MOHIST_MANAGER_BROKER!
      expect(
        (await requestLauncher(broker, ['slack', 'status'], boundary.environment().MOHIST_MANAGER_LAUNCHER!)).exitCode,
      ).toBe(0)
      expect(
        (await requestLauncher(broker, ['agent', 'list'], boundary.environment().MOHIST_MANAGER_LAUNCHER!)).exitCode,
      ).toBe(0)
      const exhausted = await requestLauncher(
        broker,
        ['slack', 'status'],
        boundary.environment().MOHIST_MANAGER_LAUNCHER!,
      )
      expect(exhausted.exitCode).toBe(126)
      expect(exhausted.stderr).toContain('management request budget is exhausted')
      expect(stub.invocations()).toHaveLength(2)
    } finally {
      await boundary.dispose()
    }
  })

  it('enforces the reply request budget', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
      requestLimits: { reply: 1, management: 64 },
    })
    try {
      const broker = boundary.environment().MOHIST_MANAGER_BROKER!
      expect(
        (
          await requestLauncher(
            broker,
            ['slack', 'message', 'send', 'hi'],
            boundary.environment().MOHIST_MANAGER_LAUNCHER!,
          )
        ).exitCode,
      ).toBe(0)
      const exhausted = await requestLauncher(
        broker,
        ['slack', 'message', 'send', 'again'],
        boundary.environment().MOHIST_MANAGER_LAUNCHER!,
      )
      expect(exhausted.exitCode).toBe(126)
      expect(exhausted.stderr).toContain('reply request budget is exhausted')
      expect(stub.invocations()).toHaveLength(1)
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
    const invocationObserver = observeInvocation(stub)
    const launcher = requestLauncher(broker, ['slack', 'status'], boundary.environment().MOHIST_MANAGER_LAUNCHER!)
    const pending = launcher.catch(() => null)
    try {
      const invocation = await invocationObserver.waitFor(launcher)
      expect(stub.invocations()).toHaveLength(1)
      const childPid = invocation.pid
      const inspectedEnvironment = readFileSync(`/proc/${childPid}/environ`, 'utf8')
      expect(inspectedEnvironment).not.toContain(grant.managementCredential)
      expect(inspectedEnvironment).not.toContain(grant.replyCredential)
      const startedAt = Date.now()
      await boundary.dispose()
      expect(Date.now() - startedAt).toBeLessThan(10_000)
      await pending
      // The boundary directory is released only after the child tree is gone.
      expect(readdirSync(join(root, 'manager-executions'))).toHaveLength(0)
    } finally {
      invocationObserver.close()
      await boundary.dispose()
      await pending
    }
  })

  it('redacts credentials split across stdout and stderr chunks', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const stub = await writeChunkedCredentialStubMo(root)
    const boundary = await ManagerExecutionBoundary.create(grant, root, {
      moExecutable: stub.executable,
      workDir: stub.frozenWorkDir,
    })
    try {
      const result = await requestLauncher(
        boundary.environment().MOHIST_MANAGER_BROKER!,
        ['slack', 'status'],
        boundary.environment().MOHIST_MANAGER_LAUNCHER!,
      )
      // A recurring failure of this test was undecidable because the launcher
      // reported neither a signal nor whether the credential child ran.
      expect(result).toMatchObject({ exitCode: 0, signal: null, stdout: '***', stderr: '***' })
      expect(stub.invocations()).toHaveLength(1)
      expect(JSON.stringify(result)).not.toContain(grant.managementCredential)
      expect(JSON.stringify(result)).not.toContain(grant.replyCredential)
    } finally {
      await boundary.dispose()
    }
  })

  it('answers a start failure with one diagnostic response', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    // A present but non-executable Manager executable fails inside `spawn`,
    // which emits `error` and then `close` for the same request.
    const notExecutable = join(root, 'mo-without-execute-permission')
    await writeFile(notExecutable, '#!/bin/sh\nexit 0\n', { encoding: 'utf8', mode: 0o600 })
    const boundary = await ManagerExecutionBoundary.create(grant, root, { moExecutable: notExecutable })
    try {
      const result = await requestLauncher(
        boundary.environment().MOHIST_MANAGER_BROKER!,
        ['slack', 'status'],
        boundary.environment().MOHIST_MANAGER_LAUNCHER!,
      )
      expect(result).toMatchObject({ exitCode: 126, signal: null, stdout: '' })
      expect(result.stderr).toContain('the Manager command could not be started')
      expect(JSON.stringify(result)).not.toContain(grant.managementCredential)
      expect(JSON.stringify(result)).not.toContain(grant.replyCredential)
    } finally {
      await boundary.dispose()
    }
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

  it('closes the boundary when the injected clock reaches expiry', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    let now = Date.now()
    const stub = await writeStubMo(root, false)
    const boundary = await ManagerExecutionBoundary.create(
      { ...grant, expiresAt: new Date(now + 1_000).toISOString() },
      root,
      { moExecutable: stub.executable, now: () => now },
    )
    now += 1_001
    try {
      expect(boundary.hasExpired()).toBe(true)
      await new Promise<void>((resolve) => setImmediate(resolve))
      expect(() => boundary.environment()).toThrow('Manager execution boundary is closed')
      expect(stub.invocations()).toHaveLength(0)
    } finally {
      await boundary.dispose()
    }
  })

  it('verifies a peer through a socket table larger than the former inspector buffer', async () => {
    const root = await mkdtemp(join(tmpdir(), 'mohist-manager-boundary-'))
    const inspector = join(root, 'ss-stub.mjs')
    const acceptedInode = '4242'
    const peerInode = '4243'
    const rows = 15_000
    const fillerRow = `u_str ESTAB 0 0 ${'x'.repeat(240)} 1 * 2 3 users:(("node",pid=11,fd=3))`
    const matchingRow = `u_str ESTAB 0 0 * ${peerInode} * ${acceptedInode} users:(("node",pid=77,fd=9))`
    await writeFile(
      inspector,
      `#!/usr/bin/env node
for (let index = 0; index < ${rows}; index++) process.stdout.write(${JSON.stringify(`${fillerRow}\n`)})
process.stdout.write(${JSON.stringify(`${matchingRow}\n`)})
setTimeout(() => process.exit(0), 20_000)
`,
      { encoding: 'utf8', mode: 0o700 },
    )
    await chmod(inspector, 0o700)

    const launcherPath = join(root, 'mo')
    const verdict = await inspectManagerPeer({ _handle: { fd: 9 } } as unknown as Socket, launcherPath, undefined, {
      platform: 'linux',
      socketInode: async (path) =>
        path === `/proc/${process.pid}/fd/9` ? acceptedInode : path === '/proc/77/fd/9' ? peerInode : null,
      scanSocketTable: async (onRow) => {
        await scanSocketInspectorRows(inspector, onRow)
      },
      readCommandLine: async (pid) => (pid === 77 ? ['/usr/bin/node', launcherPath] : []),
      samePath: async (left, right) => left === right,
    })

    // The matching row is the last row of a table larger than the fixed 4 MiB
    // inspector buffer that used to refuse a legitimate launcher on a busy
    // host. The stub outlives the match, so stopping early is part of the
    // contract: waiting for its exit would hit the test deadline instead.
    expect(verdict).toEqual({ admitted: true })
    expect(rows * (fillerRow.length + 1)).toBeGreaterThan(4 * 1024 * 1024)
  })
})

async function writeChunkedCredentialStubMo(
  root: string,
): Promise<{ executable: string; frozenWorkDir: string; invocations: () => { pid: number; args: string[] }[] }> {
  const directory = join(root, 'chunked-stub-bin')
  const executable = join(directory, 'mo')
  const frozenWorkDir = join(root, 'chunked-frozen-workdir')
  const marker = join(root, 'chunked-stub-invocations.jsonl')
  await mkdir(directory, { recursive: true })
  await mkdir(frozenWorkDir, { recursive: true })
  const script = `#!/usr/bin/env node
const fs = require('node:fs')
fs.appendFileSync(${JSON.stringify(marker)}, JSON.stringify({ pid: process.pid, args: process.argv.slice(2) }) + '\\n')
const management = ${JSON.stringify(grant.managementCredential)}
const reply = ${JSON.stringify(grant.replyCredential)}
const split = (value) => [value.slice(0, Math.ceil(value.length / 2)), value.slice(Math.ceil(value.length / 2))]
const [managementFirst, managementSecond] = split(management)
const [replyFirst, replySecond] = split(reply)
// Let pending pipe writes drain before natural exit; a timer cannot prove that.
process.stdout.write(managementFirst, () => {
  process.stderr.write(replyFirst, () => {
    setImmediate(() => {
      process.stdout.write(managementSecond)
      process.stderr.write(replySecond)
    })
  })
})
`
  await writeFile(executable, script, { encoding: 'utf8', mode: 0o700 })
  await chmod(executable, 0o700)
  return {
    executable,
    frozenWorkDir,
    invocations: () =>
      existsSync(marker)
        ? readFileSync(marker, 'utf8')
            .trim()
            .split('\n')
            .filter(Boolean)
            .map((line) => JSON.parse(line) as { pid: number; args: string[] })
        : [],
  }
}

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

interface LauncherResult {
  readonly exitCode: number | null
  readonly signal: NodeJS.Signals | null
  readonly stdout: string
  readonly stderr: string
}

function requestLauncher(brokerPath: string, args: string[], launcherPath: string): Promise<LauncherResult> {
  // tsconfig lib predates Promise.withResolvers, so the executor form stays.
  return new Promise((resolve, reject) => {
    const child = spawn(launcherPath, args, {
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
    // A signal-killed launcher must stay distinguishable from its own exit
    // code: collapsing both into 126 is what made the recurring failure
    // undecidable.
    child.once('close', (exitCode, signal) =>
      resolve({ exitCode: exitCode ?? null, signal: signal ?? null, stdout, stderr }),
    )
  })
}

interface BrokerExchange {
  readonly body: string
  readonly socketError: NodeJS.ErrnoException | null
}

function exchangeWithBroker(
  path: string,
  kind: 'management' | 'reply',
  args?: string[],
  cwd?: string,
): Promise<BrokerExchange> {
  return new Promise((resolve) => {
    const socket = createConnection(path)
    let body = ''
    let socketError: NodeJS.ErrnoException | null = null
    socket.setEncoding('utf8')
    socket.on('data', (chunk) => {
      body += chunk
    })
    socket.on('error', (error: NodeJS.ErrnoException) => {
      socketError = error
    })
    socket.on('connect', () => {
      socket.end(JSON.stringify({ kind, ...(args === undefined ? {} : { args }), ...(cwd ? { cwd } : {}) }))
    })
    socket.on('close', () => resolve({ body, socketError }))
  })
}

function parseBrokerBody(body: string): { credential?: string; exitCode?: number; stdout?: string; stderr?: string } {
  return JSON.parse(body) as { credential?: string; exitCode?: number; stdout?: string; stderr?: string }
}
