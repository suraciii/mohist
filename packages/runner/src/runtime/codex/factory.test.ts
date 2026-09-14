import { afterEach, describe, expect, it } from 'vitest'
import { withRunnerResources } from '../../system/filesystem.js'
import {
  CodexRuntime,
  createDefaultCodexRuntime,
  getCodexRuntimeFactory,
  getCodexServerFactory,
  type CodexRuntimeDeps,
  type CodexRuntimeFactory,
} from './index.js'
import type { CodexServerFactory, CodexServerHandle } from './server-process.js'

const BASE_DEPS: CodexRuntimeDeps = {
  codexHome: '/runner/.mohist/codex',
  cwd: '/work',
}

function fakeServerHandle(): CodexServerHandle {
  return {
    codexHome: '/runner/.mohist/codex',
    send: <P, R>(): Promise<R> => Promise.resolve(undefined as unknown as R),
    denyServerRequest: () => {},
    subscribe: () => () => {},
    close: async () => {},
  }
}

describe('CodexRuntime factory seam', () => {
  afterEach(async () => {
    // Tests that opt into the resource context need a clean state.
    await withRunnerResources({}, async () => {})
  })

  it('returns the resource-context override when provided', async () => {
    let received: CodexRuntimeDeps | null = null
    const override: CodexRuntimeFactory = (deps) => {
      received = deps
      return new CodexRuntime(deps)
    }
    await withRunnerResources({ codexRuntimeFactory: override }, async () => {
      const factory = getCodexRuntimeFactory()
      expect(factory).toBe(override)
      const runtime = factory(BASE_DEPS)
      expect(runtime).toBeInstanceOf(CodexRuntime)
      expect(received).toBe(BASE_DEPS)
    })
  })

  it('falls back to the default factory when no resource override is provided', () => {
    const factory = getCodexRuntimeFactory()
    expect(factory).toBe(createDefaultCodexRuntime)
    const runtime = factory(BASE_DEPS)
    expect(runtime).toBeInstanceOf(CodexRuntime)
  })

  it('createDefaultCodexRuntime wires the spawned line-framed JSON-RPC consumer', () => {
    const runtime = createDefaultCodexRuntime(BASE_DEPS)
    expect(runtime).toBeInstanceOf(CodexRuntime)
    // The default factory must not surface as a function with explicit deps either.
    const explicit = createDefaultCodexRuntime({ ...BASE_DEPS, serverFactory: async () => fakeServerHandle() })
    expect(explicit).toBeInstanceOf(CodexRuntime)
  })

  it('honours a custom CodexServerFactory injected through the resource context', async () => {
    let calledWith: { codexHome: string; cwd: string } | null = null
    const serverFactory: CodexServerFactory = async (options) => {
      calledWith = { codexHome: options.codexHome, cwd: options.cwd }
      return fakeServerHandle()
    }
    const runtimeFactory: CodexRuntimeFactory = (deps) =>
      new CodexRuntime({ ...deps, serverFactory })
    await withRunnerResources({ codexServerFactory: serverFactory, codexRuntimeFactory: runtimeFactory }, async () => {
      const factory = getCodexRuntimeFactory()
      const runtime = factory(BASE_DEPS)
      expect(runtime).toBeInstanceOf(CodexRuntime)
      const startResult = await runtime.start()
      expect(startResult).toMatchObject({ ok: true })
      expect(calledWith).toEqual({ codexHome: '/runner/.mohist/codex', cwd: '/work' })
      expect(getCodexServerFactory()).toBe(serverFactory)
    })
  })

  it('reports unavailable-runtime when the factory body is missing the spawned consumer', async () => {
    const runtime = new CodexRuntime({ ...BASE_DEPS })
    const result = await runtime.start()
    expect(result).toMatchObject({
      ok: false,
      error: {
        kind: 'unavailable-runtime',
        diagnostics: expect.arrayContaining([
          expect.objectContaining({ severity: 'error', code: 'server-spawn-failed' }),
        ]),
      },
    })
  })

  it('drives the seam deterministically across multiple start/shutdown cycles', async () => {
    let starts = 0
    const serverFactory: CodexServerFactory = async () => {
      starts += 1
      return fakeServerHandle()
    }
    const runtime = new CodexRuntime({ ...BASE_DEPS, serverFactory })
    const first = await runtime.start()
    expect(first).toMatchObject({ ok: true })
    expect(starts).toBe(1)
    await runtime.shutdown()
    const second = await runtime.start()
    expect(second).toMatchObject({ ok: true })
    expect(starts).toBe(2)
  })
})