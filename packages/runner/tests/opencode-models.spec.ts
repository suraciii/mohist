import { describe, expect, it, vi } from 'vitest'
import {
  discoverOpencodeModels,
  mergeOpencodeModelCatalogs,
  opencodeModelCatalogsEqual,
  parseOpencodeModelsVerbose,
} from '../src/runtime/opencode-models.js'
import { withTestRunnerResources } from './support/test-resources.js'
import { buildRegistrationState } from '../src/runtime/registration-state.js'
import type { CodexCatalog } from '../src/runtime/codex/types.js'

describe('OpenCode model discovery', () => {
  it('parses nested model ids, multiline metadata, and malformed metadata recovery', () => {
    const result = parseOpencodeModelsVerbose(
      [
        'openrouter/vendor/family/model',
        JSON.stringify({ variants: { high: {}, max: {} } }, null, 2),
        'broken/model',
        '{ invalid }',
        'openai/gpt-5.6-sol',
      ].join('\n'),
    )

    expect(result).toEqual({
      models: ['openrouter/vendor/family/model', 'broken/model', 'openai/gpt-5.6-sol'],
      variants: { 'openrouter/vendor/family/model': ['high', 'max'] },
    })
  })

  it('runs the configured command shell-free and preserves partial timeout output', async () => {
    const run = vi.fn(async () => ({
      exitCode: 1,
      stdout: 'openai/gpt-5.6-sol\n' + JSON.stringify({ variants: { high: {} } }),
      stderr: 'Command timed out',
      status: 'timeout' as const,
      timeoutMs: 3_000,
    }))

    await withTestRunnerResources(
      async () => {
        await expect(discoverOpencodeModels(new AbortController().signal)).resolves.toEqual({
          models: ['openai/gpt-5.6-sol'],
          variants: { 'openai/gpt-5.6-sol': ['high'] },
          complete: false,
        })
      },
      {
        environment: { MOHIST_AGENT_MODELS_COMMAND: 'custom-opencode' },
        commandRunner: { run },
      },
    )

    expect(run).toHaveBeenCalledWith(
      'custom-opencode',
      ['models', '--verbose'],
      '.',
      expect.any(AbortSignal),
      undefined,
      { timeoutMs: 3_000 },
    )
  })

  it('merges incomplete results without deleting known values', () => {
    expect(
      mergeOpencodeModelCatalogs(
        { models: ['a/one'], variants: { 'a/one': ['low'] } },
        { models: ['b/two'], variants: { 'a/one': ['high'] } },
      ),
    ).toEqual({
      models: ['a/one', 'b/two'],
      variants: { 'a/one': ['low', 'high'] },
    })
  })

  it('compares catalog content without depending on order', () => {
    expect(
      opencodeModelCatalogsEqual(
        { models: ['a/one', 'b/two'], variants: { 'a/one': ['low', 'high'] } },
        { models: ['b/two', 'a/one'], variants: { 'a/one': ['high', 'low'] } },
      ),
    ).toBe(true)
  })

  it('publishes the host-owned snapshot in runner registration', () => {
    const registration = buildRegistrationState(
      {
        projectId: 'project-a',
        environmentVersion: 'environment-a',
        environmentLoadedAt: '2026-09-18T00:00:00.000Z',
      } as never,
      null,
      { actions: [], tombstones: [] },
      () => 'connection-a',
      'process-a',
      { models: ['openai/gpt-5.6-sol'], variants: { 'openai/gpt-5.6-sol': ['high'] } },
      new Set(['opencode']),
    )

    expect(registration.runtimeCatalogs?.opencode).toEqual({
      models: ['openai/gpt-5.6-sol'],
      variants: { 'openai/gpt-5.6-sol': ['high'] },
      supportsReasoningEffort: false,
    })
    expect(registration.environmentVersion).toBe('environment-a')
    expect(registration.environmentLoadedAt).toBe('2026-09-18T00:00:00.000Z')
  })

  it('publishes a complete Codex catalog with canonical reasoning efforts', () => {
    const catalog: CodexCatalog = {
      models: [
        {
          id: 'gpt-5',
          displayName: 'GPT-5',
          reasoningEfforts: ['off', 'high'],
          defaultReasoningEffort: 'off',
          supportsReasoningEffort: true,
        },
      ],
      complete: true,
      capabilityRevision: 'codex-revision',
    }
    const registration = buildRegistrationState(
      { projectId: 'project-a' } as never,
      null,
      { actions: [], tombstones: [] },
      () => 'connection-a',
      'process-a',
      { models: [], variants: {} },
      new Set(['codex']),
      { catalog: () => catalog },
    )

    expect(registration.runtimeCatalogs?.codex).toEqual({
      models: ['gpt-5'],
      variants: {},
      reasoningEfforts: { 'gpt-5': ['off', 'high'] },
      supportsReasoningEffort: true,
      complete: true,
      capabilityRevision: 'codex-revision',
    })
  })

  it('omits the Codex catalog when Codex is not enabled even if a runtime is wired', () => {
    const registration = buildRegistrationState(
      { projectId: 'project-a' } as never,
      null,
      { actions: [], tombstones: [] },
      () => 'connection-a',
      'process-a',
      { models: [], variants: {} },
      new Set(['pi']),
      { catalog: () => codexCatalog('codex-revision') },
    )

    expect(registration.runtimeCatalogs?.codex).toBeUndefined()
  })

  it.each([
    ['no runtime is wired', undefined],
    ['the catalog is absent', null],
    ['the catalog is empty', { models: [], complete: true, capabilityRevision: 'empty' }],
    [
      'the catalog is incomplete',
      { models: codexCatalog('incomplete').models, complete: false, capabilityRevision: 'incomplete' },
    ],
  ] as const)('omits the Codex catalog when %s', (_label, catalog) => {
    const registration = buildRegistrationState(
      { projectId: 'project-a' } as never,
      null,
      { actions: [], tombstones: [] },
      () => 'connection-a',
      'process-a',
      { models: [], variants: {} },
      new Set(['codex']),
      catalog === undefined ? null : { catalog: () => catalog as CodexCatalog },
    )

    expect(registration.runtimeCatalogs?.codex).toBeUndefined()
  })
})

function codexCatalog(capabilityRevision: string): CodexCatalog {
  return {
    models: [
      {
        id: 'gpt-5',
        displayName: 'GPT-5',
        reasoningEfforts: ['off', 'high'],
        defaultReasoningEffort: 'off',
        supportsReasoningEffort: true,
      },
    ],
    complete: true,
    capabilityRevision,
  }
}
