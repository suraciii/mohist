import { AsyncLocalStorage } from 'node:async_hooks'
import { join } from 'node:path'
import { describe, expect, it as vitestIt, vi } from 'vitest'
import { SessionCommandJournal } from '../src/runtime/session-command-journal.js'
import { createSessionCommandHandler, type SessionCommandRequest } from '../src/server/session-command-handler.js'
import { createTestTempDir } from './support/temp-dir.js'
import { mkdir, writeFile } from './support/test-fs.js'
import { withTestRunnerResources } from './support/test-resources.js'

const rootStorage = new AsyncLocalStorage<string>()

function root(): string {
  const value = rootStorage.getStore()
  if (!value) throw new Error('session command journal test resource context is not active')
  return value
}

async function runInTestResources(body: () => unknown): Promise<void> {
  await withTestRunnerResources(async () => {
    await rootStorage.run(await createTestTempDir('mohist-session-command-'), async () => await body())
  })
}

const it = Object.assign((name: string, body: () => unknown) => vitestIt(name, () => runInTestResources(body)), {
  each: (table: unknown[]) => (name: string, body: (value: unknown) => unknown) =>
    vitestIt.each(table)(name, (value) => runInTestResources(() => body(value))),
}) as typeof vitestIt

function request(): SessionCommandRequest {
  return {
    sessionId: 'session-1',
    runtime: 'opencode',
    runtimeSessionId: 'runtime-1',
    runnerId: 'runner-1',
    workDir: '/work',
    command: 'compact',
    operationId: 'compact-1',
  }
}

describe('SessionCommandJournal', () => {
  it('persists completed results for a new runner process', async () => {
    const first = new SessionCommandJournal(root())
    await first.load()
    await first.start(request())
    await first.complete(request(), { ok: true })

    const restarted = new SessionCommandJournal(root())
    await restarted.load()
    await expect(restarted.get('session-1', 'compact-1')).resolves.toMatchObject({
      state: 'completed',
      result: { ok: true },
    })
  })

  it('retains started operations across restart', async () => {
    const first = new SessionCommandJournal(root())
    await first.load()
    await first.start(request())

    const restarted = new SessionCommandJournal(root())
    await restarted.load()
    await expect(restarted.get('session-1', 'compact-1')).resolves.toMatchObject({ state: 'started' })
  })

  it('fails closed for corrupt state', async () => {
    const journal = new SessionCommandJournal(root())
    const filePath = join(root(), '.mohist', 'runner-state', 'session-commands.json')
    await mkdir(join(root(), '.mohist', 'runner-state'), { recursive: true })
    await writeFile(filePath, 'not-json')
    await journal.load()

    await expect(journal.get('session-1', 'compact-1')).rejects.toThrow('unavailable')
  })

  it.each([
    { version: 1, operations: [] },
    { version: 1, operations: { 'session-1': [] } },
  ])('fails closed for parseable invalid state without invoking the runtime', async (file) => {
    const filePath = join(root(), '.mohist', 'runner-state', 'session-commands.json')
    await mkdir(join(root(), '.mohist', 'runner-state'), { recursive: true })
    await writeFile(filePath, JSON.stringify(file))
    const journal = new SessionCommandJournal(root())
    await journal.load()

    const handler = vi.fn(async () => ({ ok: true, runtimeSessionId: 'runtime-2' }))
    const invoke = createSessionCommandHandler({ handler, journal })

    const result = await invoke({
      ...request(),
      command: 'reset',
      operationId: 'reset-1',
      expectedRuntimeSessionId: 'runtime-1',
    })

    expect(result).toEqual({ ok: false, error: 'unavailable' })
    expect(handler).not.toHaveBeenCalled()
  })

  it.each([{ ok: true, error: 'missing' }, { ok: false }, { ok: true, runtimeSessionId: 'runtime-2' }])(
    'fails closed for a semantically invalid completed result after restart',
    async (result) => {
      const filePath = join(root(), '.mohist', 'runner-state', 'session-commands.json')
      await mkdir(join(root(), '.mohist', 'runner-state'), { recursive: true })
      await writeFile(
        filePath,
        JSON.stringify({
          version: 1,
          operations: {
            'session-1': {
              'compact-1': { request: request(), state: 'completed', result },
            },
          },
        }),
      )
      const journal = new SessionCommandJournal(root())
      await journal.load()

      const handler = vi.fn(async () => ({ ok: true }))
      const invoke = createSessionCommandHandler({ handler, journal })

      await expect(invoke(request())).resolves.toEqual({ ok: false, error: 'unavailable' })
      expect(handler).not.toHaveBeenCalled()
    },
  )
})
