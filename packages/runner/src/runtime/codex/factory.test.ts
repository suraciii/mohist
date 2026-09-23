import { afterEach, describe, expect, it } from 'vitest'
import { withRunnerResources } from '../../system/filesystem.js'
import {
  CodexRuntime,
  createDefaultCodexRuntime,
  getCodexRuntimeFactory,
  type CodexRuntimeDeps,
  type CodexRuntimeFactory,
} from './index.js'
import { getCodexServerFactory } from './factory.js'
import type { CodexAuthenticationProbe, CodexCatalogLoader, CodexCliProbe, CodexReadinessProbe } from './readiness.js'
import type { CodexServerFactory, CodexServerHandle } from './server-process.js'
import * as codexPublicSurface from './index.js'

const BASE_DEPS: CodexRuntimeDeps = {
  codexHome: '/runner/.mohist/codex',
  cwd: '/work',
}

/**
 * A fake server handle that emits the canonical `initialize` response
 * and a one-model `model/list` response. Tests that need to assert
 * the factory seam end-to-end supply this; tests that exercise the
 * protocol-error boundaries supply their own (or use the
 * `protocol-failure` listener).
 */
function fakeServerHandle(overrides: Partial<CodexServerHandle> = {}): CodexServerHandle {
  return {
    codexHome: '/runner/.mohist/codex',
    async send<P, R>(request: { readonly method: string; readonly params?: P; readonly id: number }): Promise<R> {
      if (request.method === 'initialize') {
        return {
          jsonrpc: '2.0',
          id: request.id,
          result: {
            protocolVersion: 'v2',
            codexHome: '/runner/.mohist/codex',
            userAgent: 'codex/0.153.0',
          },
        } as unknown as R
      }
      if (request.method === 'model/list') {
        return {
          jsonrpc: '2.0',
          id: request.id,
          result: { models: [{ id: 'gpt-5' }], complete: true },
        } as unknown as R
      }
      throw new Error(`Unexpected method ${request.method}`)
    },
    notify: <P>(): boolean => true,
    denyServerRequest: () => {},
    subscribe: () => () => {},
    close: async () => {},
    ...overrides,
  }
}

/**
 * A minimal readiness probe that succeeds for every check. Tests that
 * exercise readiness boundaries use a more specific probe.
 */
function passingReadinessProbe(): CodexReadinessProbe {
  const cli: CodexCliProbe = {
    async resolveCodexBinary() {
      return '/usr/local/bin/codex'
    },
    async resolveCodexVersion() {
      return '0.153.0'
    },
  }
  const authentication: CodexAuthenticationProbe = {
    async hasManagedAuthentication() {
      return true
    },
  }
  const catalog: CodexCatalogLoader = {
    async loadCatalog() {
      return null
    },
  }
  return { cli, authentication, catalog }
}

function withPassingReadinessProbe(deps: CodexRuntimeDeps): CodexRuntimeDeps {
  return { ...deps, readinessProbe: passingReadinessProbe() }
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
    const explicit = createDefaultCodexRuntime({
      ...BASE_DEPS,
      serverFactory: async () => fakeServerHandle(),
    })
    expect(explicit).toBeInstanceOf(CodexRuntime)
  })

  it('honours a custom CodexServerFactory injected through the resource context', async () => {
    let calledWith: { codexHome: string; cwd: string } | null = null
    const serverFactory: CodexServerFactory = async (options) => {
      calledWith = { codexHome: options.codexHome, cwd: options.cwd }
      return fakeServerHandle()
    }
    const runtimeFactory: CodexRuntimeFactory = (deps) =>
      new CodexRuntime(withPassingReadinessProbe({ ...deps, serverFactory }))
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

  it('uses the resource-context server factory from the default factory seam', async () => {
    let starts = 0
    const serverFactory: CodexServerFactory = async () => {
      starts += 1
      return fakeServerHandle()
    }

    await withRunnerResources({ codexServerFactory: serverFactory }, async () => {
      const runtime = createDefaultCodexRuntime(withPassingReadinessProbe(BASE_DEPS))
      const result = await runtime.start()
      expect(result).toMatchObject({ ok: true })
      await runtime.shutdown()
    })

    expect(starts).toBe(1)
  })

  it('keeps Codex JSON-RPC shapes out of the public barrel', () => {
    for (const protocolExport of [
      'CODEX_LOCKED_METHODS',
      'isCodexLockedMethod',
      'isCodexInitializeRequest',
      'isCodexTurnCompletedEvent',
    ]) {
      expect(codexPublicSurface).not.toHaveProperty(protocolExport)
    }
  })

  it('reports unavailable-runtime when the factory body is missing the spawned consumer', async () => {
    const runtime = new CodexRuntime(withPassingReadinessProbe(BASE_DEPS))
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
    const runtime = new CodexRuntime(withPassingReadinessProbe({ ...BASE_DEPS, serverFactory }))
    const first = await runtime.start()
    expect(first).toMatchObject({ ok: true })
    expect(starts).toBe(1)
    await runtime.shutdown()
    const second = await runtime.start()
    expect(second).toMatchObject({ ok: true })
    expect(starts).toBe(2)
  })
})
