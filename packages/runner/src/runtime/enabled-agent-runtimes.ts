import type { ActionCatalog } from '../actions/manifest.js'
import type { AgentRuntime } from '../core/types.js'

const DEFAULT_AGENT_RUNTIMES: readonly AgentRuntime[] = ['pi']
const KNOWN_AGENT_RUNTIMES = new Set<AgentRuntime>(['pi', 'opencode'])

export function parseEnabledAgentRuntimes(value: string | undefined): readonly AgentRuntime[] {
  if (value === undefined) return DEFAULT_AGENT_RUNTIMES
  return [...normalizeEnabledAgentRuntimes(value.split(','))]
}

export function normalizeEnabledAgentRuntimes(values: readonly string[] | undefined): ReadonlySet<AgentRuntime> {
  const candidates = values ?? DEFAULT_AGENT_RUNTIMES
  const enabled = new Set<AgentRuntime>()
  for (const value of candidates) {
    const runtime = value.trim().toLowerCase()
    if (!KNOWN_AGENT_RUNTIMES.has(runtime as AgentRuntime)) {
      throw new Error(
        runtime.length === 0
          ? 'ENABLED_AGENT_RUNTIMES must contain at least one Runtime'
          : `ENABLED_AGENT_RUNTIMES contains unknown Runtime '${value}'`,
      )
    }
    enabled.add(runtime as AgentRuntime)
  }
  if (enabled.size === 0) throw new Error('ENABLED_AGENT_RUNTIMES must contain at least one Runtime')
  return enabled
}

export function actionCatalogForEnabledRuntimes(
  catalog: ActionCatalog,
  enabled: ReadonlySet<AgentRuntime>,
): ActionCatalog {
  return {
    ...catalog,
    actions: catalog.actions.filter((action) => {
      if (action.name === 'mohist/pi') return enabled.has('pi')
      if (action.name === 'mohist/opencode') return enabled.has('opencode')
      return true
    }),
  }
}
