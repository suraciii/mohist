import { describe, expect, it } from 'vitest'
import {
  createCodexModelCatalogLoader,
  mapCodexCanonicalReasoningEffort,
  mapCodexNativeReasoningEffort,
  validateCodexTurnConfiguration,
  type CodexModelListTransport,
} from './model-catalog.js'
import { normalizeUnsupportedExecutionConfigurationCodex } from './errors.js'
import type { CodexCatalog } from './types.js'

function catalogTransport(
  responses: readonly unknown[],
  calls: Array<{ readonly cursor?: string | null; readonly pageSize?: number }>,
): CodexModelListTransport {
  let index = 0
  return {
    async send<P, R>(request: { readonly params?: P }) {
      calls.push(request.params as { readonly cursor?: string | null; readonly pageSize?: number })
      const response = responses[index]
      index += 1
      if (response instanceof Error) throw response
      return response as R
    },
  }
}

function modelCatalog(): CodexCatalog {
  return {
    models: [
      {
        id: 'gpt-5',
        displayName: 'GPT-5',
        reasoningEfforts: ['off', 'low', 'high'],
        defaultReasoningEffort: 'off',
        supportsReasoningEffort: true,
      },
    ],
    complete: true,
    capabilityRevision: 'revision',
  }
}

describe('Codex reasoning effort mapping', () => {
  it("maps native 'none' to canonical 'off' and back", () => {
    expect(mapCodexNativeReasoningEffort('none')).toBe('off')
    expect(mapCodexCanonicalReasoningEffort('off')).toBe('none')
  })

  it('maps the other supported values by exact name', () => {
    for (const effort of ['minimal', 'low', 'medium', 'high', 'xhigh', 'max']) {
      expect(mapCodexNativeReasoningEffort(effort)).toBe(effort)
      expect(mapCodexCanonicalReasoningEffort(effort)).toBe(effort)
    }
  })

  it('keeps unknown native efforts as diagnostics without publishing them', async () => {
    const calls: Array<{ readonly cursor?: string | null; readonly pageSize?: number }> = []
    const loader = createCodexModelCatalogLoader(
      catalogTransport(
        [
          {
            models: [
              {
                id: 'gpt-5',
                reasoningEfforts: ['none', 'future-effort'],
                defaultReasoningEffort: 'future-effort',
              },
            ],
            complete: true,
          },
        ],
        calls,
      ),
    )

    const result = await loader.refreshCatalog()
    expect(result.ok).toBe(true)
    expect(result.catalog?.models[0]?.reasoningEfforts).toEqual(['off'])
    expect(result.diagnostics.map((diagnostic) => diagnostic.code)).toEqual([
      'catalog-unknown-reasoning-effort',
      'catalog-unknown-default-reasoning-effort',
    ])
  })
})

describe('Codex model catalog refresh', () => {
  it('accepts the official data response and object reasoning effort options', async () => {
    const loader = createCodexModelCatalogLoader(
      catalogTransport(
        [
          {
            data: [
              {
                id: 'gpt-6-astra',
                displayName: 'GPT-6-Astra',
                supportedReasoningEfforts: [
                  { reasoningEffort: 'low', description: 'fast' },
                  { reasoningEffort: 'high', description: 'deep' },
                ],
                defaultReasoningEffort: 'low',
              },
            ],
            nextCursor: null,
          },
        ],
        [],
      ),
    )
    const result = await loader.refreshCatalog()
    expect(result.ok).toBe(true)
    expect(result.catalog?.models[0]?.id).toBe('gpt-6-astra')
    expect(result.catalog?.models[0]?.reasoningEfforts).toEqual(['low', 'high'])
    expect(result.catalog?.models[0]?.defaultReasoningEffort).toBe('low')
  })

  it('pages model/list and replaces the snapshot only after a complete non-empty merge', async () => {
    const calls: Array<{ readonly cursor?: string | null; readonly pageSize?: number }> = []
    const loader = createCodexModelCatalogLoader(
      catalogTransport(
        [
          {
            models: [{ id: 'gpt-5', reasoningEfforts: ['none', 'low'] }],
            complete: false,
            nextCursor: 'page-2',
          },
          {
            models: [{ id: 'o3', reasoningEfforts: ['high'] }],
            complete: true,
          },
        ],
        calls,
      ),
      { pageSize: 2 },
    )

    const result = await loader.refreshCatalog()
    expect(result.ok).toBe(true)
    expect(result.changed).toBe(true)
    expect(result.catalog?.models.map((model) => model.id)).toEqual(['gpt-5', 'o3'])
    expect(result.catalog?.models[0]?.reasoningEfforts).toEqual(['off', 'low'])
    expect(result.catalog?.models[1]?.reasoningEfforts).toEqual(['high'])
    expect(calls).toEqual([
      { limit: 2, includeHidden: false },
      { cursor: 'page-2', limit: 2, includeHidden: false },
    ])
  })

  it('retains the last complete snapshot when a refresh becomes empty', async () => {
    const calls: Array<{ readonly cursor?: string | null; readonly pageSize?: number }> = []
    const loader = createCodexModelCatalogLoader(
      catalogTransport(
        [
          { models: [{ id: 'gpt-5', reasoningEfforts: ['none'] }], complete: true },
          { models: [], complete: true },
        ],
        calls,
      ),
    )

    const first = await loader.refreshCatalog()
    const second = await loader.refreshCatalog()

    expect(second.ok).toBe(false)
    expect(second.changed).toBe(false)
    expect(second.catalog).toEqual(first.catalog)
    expect(second.diagnostics[0]).toMatchObject({ code: 'catalog-empty' })
    expect(loader.catalog()).toEqual(first.catalog)
    expect(loader.diagnostic()?.code).toBe('catalog-empty')
  })

  it('retains the last complete snapshot and emits the current failure diagnostic', async () => {
    const calls: Array<{ readonly cursor?: string | null; readonly pageSize?: number }> = []
    const loader = createCodexModelCatalogLoader(
      catalogTransport(
        [{ models: [{ id: 'gpt-5', reasoningEfforts: ['none'] }], complete: true }, new Error('provider offline')],
        calls,
      ),
    )

    const first = await loader.refreshCatalog()
    const second = await loader.refreshCatalog()

    expect(first.catalog).not.toBeNull()
    expect(second.ok).toBe(false)
    expect(second.changed).toBe(false)
    expect(second.catalog).toEqual(first.catalog)
    expect(second.diagnostics[0]).toMatchObject({
      code: 'catalog-refresh-failed',
      message: 'Codex model catalog refresh failed: provider offline',
    })
    expect(loader.catalog()).toEqual(first.catalog)
    expect(loader.diagnostic()?.code).toBe('catalog-refresh-failed')
  })
})

describe('Codex turn-start configuration validation', () => {
  it("maps canonical 'off' to native 'none' at turn/start", () => {
    const result = validateCodexTurnConfiguration(modelCatalog(), {
      model: 'gpt-5',
      reasoningEffort: 'off',
    })

    expect(result).toMatchObject({
      ok: true,
      value: {
        model: 'gpt-5',
        reasoningEffort: 'off',
        nativeReasoningEffort: 'none',
      },
    })
  })

  it('validates model and effort again at turn/start', () => {
    expect(validateCodexTurnConfiguration(modelCatalog(), { model: 'missing' })).toMatchObject({
      ok: false,
      error: { kind: 'invalid-input' },
    })
    expect(validateCodexTurnConfiguration(modelCatalog(), { model: 'gpt-5', reasoningEffort: 'medium' })).toMatchObject(
      {
        ok: false,
        error: { kind: 'invalid-input' },
      },
    )
  })

  it('rejects variant as unsupported execution configuration without fallback', () => {
    const result = validateCodexTurnConfiguration(modelCatalog(), {
      model: 'gpt-5',
      variant: 'balanced',
    })
    const expected = normalizeUnsupportedExecutionConfigurationCodex(
      'Codex does not support execution variants; remove options.variant before starting the turn',
    )

    expect(result).toEqual({ ok: false, error: expected, diagnostics: expected.diagnostics })
  })
})
