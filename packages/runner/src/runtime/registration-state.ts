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
  processGeneration: string,
): RunnerRegistration {
  const piCatalog = piRuntime?.catalog()
  const piModels = piCatalog?.models.map((model) => `${model.provider}/${model.id}`) ?? []
  const piReasoningEfforts = Object.fromEntries(
    piCatalog?.models.map((model) => [`${model.provider}/${model.id}`, [...model.thinkingLevels]]) ?? [],
  )
  const piCapabilityRevision = piCatalog
    ? createHash('sha256')
        .update(JSON.stringify({ models: piModels, reasoningEfforts: piReasoningEfforts }))
        .digest('hex')
    : null
  const managerCapabilitiesAvailable = process.platform === 'linux' && piCatalog !== null
  return {
    processGeneration,
    capabilities: [
      'execution-source-v1',
      ...(managerCapabilitiesAvailable
        ? [
            'manager-execution-grant-v1',
            'manager-deployment-epoch-v1',
            'manager-private-broker-v1',
            'manager-pi-scoped-executor-v1',
            'manager-opencode-isolated-v1',
            'manager-redaction-v1',
          ]
        : []),
    ],
    actionCatalog: actionsCatalog,
    projectId: options.projectId,
    connectionId: getConnectionId(),
    runtimeCatalogs: {
      // The OpenCode runtime has no native reasoning-effort support and this
      // host performs no model discovery for it: an empty catalog means the
      // runtime validates any model/variant at execution time. The Pi entry is
      // published only once its catalog has actually loaded (issue-557 T-003).
      opencode: {
        models: [],
        variants: {},
        supportsReasoningEffort: false,
      },
      ...(piCatalog
        ? {
            pi: {
              models: piModels,
              variants: {},
              reasoningEfforts: piReasoningEfforts,
              supportsReasoningEffort: true,
              complete: true,
              capabilityRevision: piCapabilityRevision,
            },
          }
        : {}),
    },
  }
}
