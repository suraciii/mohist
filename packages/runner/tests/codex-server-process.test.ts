import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createSpawnedCodexServer,
  DEFAULT_CODEX_SHUTDOWN_TIMEOUT_MS,
  DEFAULT_CODEX_STARTUP_TIMEOUT_MS,
  type CodexServerHandle,
  type CodexServerFactoryOptions,
} from '../src/runtime/codex/server-process.js'
import { FakeChildProcess } from './support/fake-process.js'
import type { ProcessSpawner } from '../src/system/process.js'

/**
 * Encoding helper used by the fake child: every JSON-RPC envelope is
 * written as one UTF-8 line with a trailing newline. The line-framed
 * consumer in `server-process.ts` consumes each line individually.
 */
function encode(envelope: unknown): Buffer {
  return Buffer.from(`${JSON.stringify(envelope)}\n`, 'utf8')
}

type SpawnOptions = Parameters<ProcessSpawner>[2]
type SpawnedChild = ReturnType<ProcessSpawner>

type WritableSide = {
  write(chunk: string | Buffer, cb?: (err?: Error | null) => void): boolean
  end(): void
}

class FakeCodexChild extends FakeChildProcess {
  /** Records every write so tests can assert the framed envelopes. */
  readonly writes: string[] = []

  constructor(pid = 4242) {
    super(pid)
    // Replace stdin with a simple line buffer so tests can assert on
    // the framed envelopes. The spawner checks for a `Writable`-like
    // surface; our stub matches the parts the runtime touches.
    const stdin: WritableSide = {
      write: (chunk: string | Buffer, cb?: (err?: Error | null) => void) => {
        const text = typeof chunk === 'string' ? chunk : chunk.toString('utf8')
        for (const line of text.split('\n')) {
          if (line.length > 0) this.writes.push(line)
        }
        cb?.(null)
        return true
      },
      end: () => undefined,
    }
    ;(this as unknown as { stdin: WritableSide }).stdin = stdin
    // `setEncoding` is called by `createSpawnedCodexServer`; record
    // and ignore so the call is a no-op.
    ;(this.stdout as unknown as { setEncoding: (encoding: BufferEncoding) => void }).setEncoding = () => undefined
    ;(this.stderr as unknown as { setEncoding: (encoding: BufferEncoding) => void }).setEncoding = () => undefined
  }
}

interface FakeSpawner {
  readonly child: FakeCodexChild
  readonly calls: Array<{ command: string; args: string[]; options: SpawnOptions }>
  readonly spawn: (command: string, args: string[], options: SpawnOptions) => SpawnedChild
}

function buildSpawner(): FakeSpawner {
  const child = new FakeCodexChild()
  const calls: Array<{ command: string; args: string[]; options: SpawnOptions }> = []
  const spawn = (command: string, args: string[], options: SpawnOptions): SpawnedChild => {
    calls.push({ command, args: [...args], options })
    return child as unknown as SpawnedChild
  }
  return { child, calls, spawn }
}

const BASE_OPTIONS: Omit<CodexServerFactoryOptions, 'spawner'> = {
  codexHome: '/runner/.mohist/codex',
  cwd: '/work',
  // Tests use a tight shutdown budget so `close()` returns within the
  // vitest default timeout even when the fake child never emits exit.
  shutdownTimeoutMs: 25,
}

/**
 * `close()` against a fake child waits for the bounded shutdown
 * deadline; in tests where we do not care about exit semantics we
 * emit `exit` synchronously so the `close()` promise resolves
 * immediately.
 */
async function shutdownHandle(handle: CodexServerHandle, child: FakeCodexChild): Promise<void> {
  child.emit('exit', 0, null)
  await handle.close()
}

describe('createSpawnedCodexServer', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('spawns codex app-server with --stdio, no shell, and the managed CODEX_HOME', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    expect(spawner.calls).toHaveLength(1)
    const call = spawner.calls[0]!
    expect(call.command).toBe('codex')
    expect(call.args).toEqual(['app-server', '--stdio'])
    expect((call.options as { shell?: boolean }).shell).not.toBe(true)
    const env = (call.options as { env: NodeJS.ProcessEnv }).env
    expect(env.CODEX_HOME).toBe('/runner/.mohist/codex')
    expect(handle.codexHome).toBe('/runner/.mohist/codex')
    await shutdownHandle(handle, spawner.child)
  })

  it('drives the line-framed JSON-RPC consumer happy path', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    const pending = handle.send<{ cwd: string }, unknown>({
      id: 1,
      method: 'thread/start',
      params: { cwd: '/work' },
    })
    expect(spawner.child.writes).toHaveLength(1)
    const written = JSON.parse(spawner.child.writes[0]!)
    expect(written).toMatchObject({
      id: 1,
      method: 'thread/start',
      params: { cwd: '/work' },
    })
    spawner.child.writeStdout(encode({ jsonrpc: '2.0', id: 1, result: { threadId: 'thr_1', cwd: '/work' } }))
    await expect(pending).resolves.toEqual({ threadId: 'thr_1', cwd: '/work' })
    await shutdownHandle(handle, spawner.child)
  })

  it('rejects duplicate response ids as a protocol failure', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    const first = handle.send<unknown, unknown>({ id: 7, method: 'thread/start', params: { cwd: '/work' } })
    spawner.child.writeStdout(encode({ jsonrpc: '2.0', id: 7, result: { threadId: 'thr_1', cwd: '/work' } }))
    await expect(first).resolves.toEqual({ threadId: 'thr_1', cwd: '/work' })
    const second = handle.send<unknown, unknown>({ id: 7, method: 'thread/start', params: { cwd: '/work' } })
    spawner.child.writeStdout(encode({ jsonrpc: '2.0', id: 7, result: { threadId: 'thr_2', cwd: '/work' } }))
    await expect(second).rejects.toThrow(/duplicate response id 7/)
    await shutdownHandle(handle, spawner.child)
  })

  it('rejects malformed stdout as a protocol failure boundary', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    const pending = handle.send<unknown, unknown>({ id: 9, method: 'thread/start', params: { cwd: '/work' } })
    spawner.child.writeStdout(Buffer.from('{not valid json\n', 'utf8'))
    await expect(pending).rejects.toThrow(/malformed JSON-RPC line/)
    await shutdownHandle(handle, spawner.child)
  })

  it('rejects a child exit during a pending request', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    const pending = handle.send<unknown, unknown>({ id: 11, method: 'thread/start', params: { cwd: '/work' } })
    spawner.child.emit('exit', 137, 'SIGKILL')
    await expect(pending).rejects.toThrow(/before response/)
    await shutdownHandle(handle, spawner.child)
  })

  it('refuses to send methods outside the locked v2 subset', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    await expect(handle.send({ id: 1, method: 'apps/list', params: {} })).rejects.toThrow(
      /refusing to send method outside the locked v2 subset/,
    )
    await shutdownHandle(handle, spawner.child)
  })

  it('drives the bounded shutdown within the deadline', async () => {
    vi.useFakeTimers()
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({
      ...BASE_OPTIONS,
      shutdownTimeoutMs: 100,
      spawner: spawner.spawn,
    })
    const closePromise = handle.close()
    await vi.advanceTimersByTimeAsync(100)
    await closePromise
    expect(spawner.child.killSignals).toEqual(['SIGTERM', 'SIGKILL'])
    expect(DEFAULT_CODEX_SHUTDOWN_TIMEOUT_MS).toBeGreaterThan(100)
  })

  it('forces SIGTERM when the child has not exited before the deadline', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({
      ...BASE_OPTIONS,
      shutdownTimeoutMs: 25,
      spawner: spawner.spawn,
    })
    const killPromise = handle.close()
    await vi.waitFor(() => {
      expect(spawner.child.killSignals).toContain('SIGTERM')
    })
    spawner.child.emit('exit', null, 'SIGKILL')
    await killPromise
    expect(spawner.child.killSignals.length).toBeGreaterThanOrEqual(1)
  })

  it('delivers notifications without expecting a response', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    const ok = handle.notify({ method: 'initialized' })
    expect(ok).toBe(true)
    expect(spawner.child.writes).toHaveLength(1)
    const written = JSON.parse(spawner.child.writes[0]!)
    expect(written).toMatchObject({ method: 'initialized' })
    expect(written.id).toBeUndefined()
    await shutdownHandle(handle, spawner.child)
  })

  it('routes notifications to subscribers', async () => {
    const spawner = buildSpawner()
    const handle: CodexServerHandle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    const received: unknown[] = []
    handle.subscribe((message) => received.push(message))
    spawner.child.writeStdout(
      encode({ jsonrpc: '2.0', method: 'thread/status', params: { threadId: 'thr_1', status: 'idle' } }),
    )
    expect(received).toHaveLength(1)
    expect(received[0]).toMatchObject({ method: 'thread/status' })
    await shutdownHandle(handle, spawner.child)
  })

  it('writes server-initiated denial envelopes without leaking credentials', async () => {
    const spawner = buildSpawner()
    const handle = await createSpawnedCodexServer({ ...BASE_OPTIONS, spawner: spawner.spawn })
    handle.denyServerRequest(42, 'denied-by-mohist')
    const denied = JSON.parse(spawner.child.writes[spawner.child.writes.length - 1]!)
    expect(denied).toMatchObject({
      id: 42,
      result: { ok: false, denied: true, reason: 'denied-by-mohist' },
    })
    await shutdownHandle(handle, spawner.child)
  })

  it('honours the bounded startup and shutdown timeout constants', () => {
    expect(DEFAULT_CODEX_STARTUP_TIMEOUT_MS).toBe(10_000)
    expect(DEFAULT_CODEX_SHUTDOWN_TIMEOUT_MS).toBe(5_000)
  })
})
