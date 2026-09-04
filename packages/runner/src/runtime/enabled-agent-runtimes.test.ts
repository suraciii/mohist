import { describe, expect, it } from 'vitest'
import type { ActionCatalog } from '../actions/manifest.js'
import {
  actionCatalogForEnabledRuntimes,
  normalizeEnabledAgentRuntimes,
  parseEnabledAgentRuntimes,
} from './enabled-agent-runtimes.js'

describe('enabled Agent Runtimes', () => {
  it('defaults ENABLED_AGENT_RUNTIMES to Pi', () => {
    expect([...parseEnabledAgentRuntimes(undefined)]).toEqual(['pi'])
  })

  it('parses a comma-separated list case-insensitively and removes duplicates', () => {
    expect([...parseEnabledAgentRuntimes(' Pi,opencode,PI ')]).toEqual(['pi', 'opencode'])
  })

  it.each(['', ' ', ',', 'pi,', ',pi'])('rejects an empty Runtime entry in %j', (value) => {
    expect(() => parseEnabledAgentRuntimes(value)).toThrow('ENABLED_AGENT_RUNTIMES must contain at least one Runtime')
  })

  it('rejects an unknown Runtime instead of guessing', () => {
    expect(() => parseEnabledAgentRuntimes('pi,codex')).toThrow(
      "ENABLED_AGENT_RUNTIMES contains unknown Runtime 'codex'",
    )
  })

  it('publishes only Actions backed by enabled runtimes and leaves shared Actions intact', () => {
    const catalog: ActionCatalog = {
      actions: [
        { name: 'mohist/opencode', inputs: [], outputs: [], errors: [] },
        { name: 'mohist/pi', inputs: [], outputs: [], errors: [] },
        { name: 'mohist/github-pr', inputs: [], outputs: [], errors: [] },
      ],
      tombstones: [{ name: 'mohist/legacy', guidance: 'Use a supported Action' }],
    }

    expect(actionCatalogForEnabledRuntimes(catalog, normalizeEnabledAgentRuntimes(['pi']))).toEqual({
      actions: [
        { name: 'mohist/pi', inputs: [], outputs: [], errors: [] },
        { name: 'mohist/github-pr', inputs: [], outputs: [], errors: [] },
      ],
      tombstones: catalog.tombstones,
    })
    expect(actionCatalogForEnabledRuntimes(catalog, normalizeEnabledAgentRuntimes(['pi', 'opencode']))).toEqual(catalog)
  })
})
