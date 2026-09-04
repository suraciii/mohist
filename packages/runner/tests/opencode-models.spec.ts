import { describe, expect, it, vi } from 'vitest'
import {
  discoverOpencodeModels,
  mergeOpencodeModelCatalogs,
  opencodeModelCatalogsEqual,
  parseOpencodeModelsVerbose,
} from '../src/runtime/opencode-models.js'
import { withTestRunnerResources } from './support/test-resources.js'
import { buildRegistrationState } from '../src/runtime/registration-state.js'

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
      { projectId: 'project-a' } as never,
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
  })
})
