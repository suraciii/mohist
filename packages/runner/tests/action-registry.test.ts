import { describe, expect, it } from 'vitest'
import { ActionRegistry, ActionRegistryConstructionError, createDefaultRegistry } from '../src/actions/registry.js'
import type { ActionDefinition } from '../src/actions/manifest.js'

describe('ActionRegistry', () => {
  it('rejects a definition with an invalid manifest even when it bypasses defineAction', () => {
    const definition = {
      manifest: {
        name: 'test/action',
        inputs: { value: { types: [] } },
        outputs: [],
        errors: [],
      },
      run: async () => ({ output: null }),
    } as unknown as ActionDefinition

    expect(() => new ActionRegistry([definition])).toThrow(ActionRegistryConstructionError)
  })

  it('preserves manifest capabilities in the JSON action catalog', () => {
    const catalog = JSON.parse(JSON.stringify(createDefaultRegistry().catalog())) as {
      actions: Array<{ name: string; capabilities?: string[] }>
    }

    expect(catalog.actions.find((action) => action.name === 'mohist/opencode')?.capabilities).toEqual(['agent-turn'])
    expect(catalog.actions.find((action) => action.name === 'mohist/pi')?.capabilities).toEqual(['agent-turn'])
    expect(catalog.actions.find((action) => action.name === 'mohist/openspec-tasks')?.capabilities).toEqual([
      'add-tasks',
    ])
  })
})
