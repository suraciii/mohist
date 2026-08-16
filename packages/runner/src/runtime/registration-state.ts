import { createHash } from 'node:crypto'
import type { RunnerOptions, RunnerRegistration } from '../core/types.js'
import type { PiCatalog } from './pi/types.js'

export interface RegistrationPiCatalogSource {
  catalog: () => PiCatalog | null
}

export function buildRegistrationState(
  options: RunnerOptions,
  piRuntime: RegistrationPiCatalogSource | null,
  actionsCatalog: RunnerRegistration['actionCatalog'],
  getConnectionId: () => string | null,
): RunnerRegistration {
  const piCatalog = piRuntime?.catalog()
  const piModels = piCatalog?.models.map((model) => `${model.provider}/${model.id}`) ?? []
  const piReasoningEfforts = Object.fromEntries(
    piCatalog?.models.map((model) => [`${model.provider}/${model.id}`, [...model.thinkingLevels]]) ?? [],
  )
  const piCapabilityRevision = piCatalog
    ? createHash('sha256')
        .update(JSON.stringify({ models: piModels, variants: piReasoningEfforts }))
        .digest('hex')
    : null
  return {
    capabilities: [],
    actionCatalog: actionsCatalog,
    projectId: options.projectId,
    connectionId: getConnectionId(),
    ...(piCatalog
      ? {
          runtimeCatalogs: {
            pi: {
              models: piModels,
              variants: piReasoningEfforts,
              supportsReasoningEffort: true,
              complete: true,
              capabilityRevision: piCapabilityRevision,
            },
          },
        }
      : {}),
  }
}
