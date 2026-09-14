import { describe, expect, it } from 'vitest'
import {
  evaluateCodexReadiness,
  isCodexVersionSupported,
  type CodexAuthenticationProbe,
  type CodexCatalogLoader,
  type CodexCliProbe,
  type CodexReadinessProbe,
} from './readiness.js'
import type { CodexCatalog } from './types.js'

const MANAGED_CODEX_HOME = '/runner/.mohist/codex'

function cliProbe(binary: string | null, version: string | null): CodexCliProbe {
  return {
    async resolveCodexBinary() {
      return binary
    },
    async resolveCodexVersion() {
      return version
    },
  }
}

function authenticationProbe(present: boolean): CodexAuthenticationProbe {
  return {
    async hasManagedAuthentication() {
      return present
    },
  }
}

function catalogLoader(catalog: CodexCatalog | null): CodexCatalogLoader {
  return {
    async loadCatalog() {
      return catalog
    },
  }
}

function buildProbe(args: {
  binary?: string | null
  version?: string | null
  authenticated?: boolean
  catalog?: CodexCatalog | null
}): CodexReadinessProbe {
  // Default to a passing CLI version; explicit `null` propagates so
  // individual tests can drive the failure boundaries.
  const binary = args.binary === undefined ? '/usr/local/bin/codex' : args.binary
  const version = args.version === undefined ? '0.153.0' : args.version
  return {
    cli: cliProbe(binary, version),
    authentication: authenticationProbe(args.authenticated ?? true),
    catalog: catalogLoader(
      args.catalog === undefined
        ? {
            models: [
              {
                id: 'gpt-5',
                displayName: null,
                reasoningEfforts: [],
                defaultReasoningEffort: null,
                supportsReasoningEffort: true,
              },
            ],
            complete: true,
            capabilityRevision: 'rev-1',
          }
        : args.catalog,
    ),
  }
}

describe('isCodexVersionSupported', () => {
  it('accepts versions inside the locked range', () => {
    expect(isCodexVersionSupported('0.153.0', '0.153.0', '0.154.0')).toBe(true)
    expect(isCodexVersionSupported('0.153.7', '0.153.0', '0.154.0')).toBe(true)
  })

  it('rejects versions outside the locked range', () => {
    expect(isCodexVersionSupported('0.152.99', '0.153.0', '0.154.0')).toBe(false)
    expect(isCodexVersionSupported('0.154.0', '0.153.0', '0.154.0')).toBe(false)
    expect(isCodexVersionSupported('1.0.0', '0.153.0', '0.154.0')).toBe(false)
    expect(isCodexVersionSupported('0.153.0-beta', '0.153.0', '0.154.0')).toBe(true)
  })

  it('rejects unparseable versions', () => {
    expect(isCodexVersionSupported('not-a-version', '0.153.0', '0.154.0')).toBe(false)
    expect(isCodexVersionSupported('', '0.153.0', '0.154.0')).toBe(false)
  })
})

describe('evaluateCodexReadiness', () => {
  it('returns ready when every probe passes', async () => {
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe: buildProbe({}),
    })
    expect(result).toMatchObject({ ok: true })
    if (!result.ok) throw new Error('expected ok')
    expect(result.value.cliVersion).toBe('0.153.0')
    expect(result.value.codexHome).toBe(MANAGED_CODEX_HOME)
    expect(result.value.catalog.models).toHaveLength(1)
  })

  it('reports cli-not-executable when the probe cannot resolve the binary', async () => {
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe: buildProbe({ binary: null }),
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics[0]?.code).toBe('cli-not-executable')
  })

  it('reports cli-version-unknown when the binary exists but the version is unknown', async () => {
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe: buildProbe({ version: null }),
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics[0]?.code).toBe('cli-version-unknown')
  })

  it('reports incompatible-runtime when the version falls outside the locked range', async () => {
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe: buildProbe({ version: '0.152.0' }),
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'incompatible-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics[0]?.code).toBe('cli-version-incompatible')
  })

  it('reports incompatible-runtime when the version crosses the upper bound', async () => {
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe: buildProbe({ version: '0.154.0' }),
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'incompatible-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics[0]?.code).toBe('cli-version-incompatible')
  })

  it('reports managed-auth-missing when authentication is absent', async () => {
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe: buildProbe({ authenticated: false }),
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics[0]?.code).toBe('managed-auth-missing')
  })

  it('reports catalog-empty when the catalog loader returns null', async () => {
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe: buildProbe({ catalog: null }),
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics[0]?.code).toBe('catalog-empty')
  })

  it('reports catalog-empty when the catalog has no models', async () => {
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe: buildProbe({
        catalog: { models: [], complete: true, capabilityRevision: 'rev' },
      }),
    })
    expect(result).toMatchObject({
      ok: false,
      error: { kind: 'unavailable-runtime' },
    })
    if (result.ok) throw new Error('expected failure')
    expect(result.error.diagnostics[0]?.code).toBe('catalog-empty')
  })

  it('short-circuits before catalog when an earlier probe fails', async () => {
    let catalogCalls = 0
    const probe: CodexReadinessProbe = {
      cli: cliProbe(null, null),
      authentication: authenticationProbe(true),
      catalog: {
        async loadCatalog() {
          catalogCalls += 1
          return null
        },
      },
    }
    const result = await evaluateCodexReadiness({
      managedCodexHome: MANAGED_CODEX_HOME,
      startupTimeoutMs: 5_000,
      probe,
    })
    expect(result).toMatchObject({ ok: false })
    expect(catalogCalls).toBe(0)
  })
})
