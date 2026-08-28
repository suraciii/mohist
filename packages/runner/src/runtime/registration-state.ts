import { createHash } from 'node:crypto'
import type { RunnerOptions, RunnerRegistration } from '../core/types.js'
import type { PiCatalog } from './pi/types.js'
import type { OpencodeModelCatalog } from './opencode-models.js'

export interface RegistrationPiCatalogSource {
  catalog: () => PiCatalog | null
}

export function buildRegistrationState(
  options: RunnerOptions,
  piRuntime: RegistrationPiCatalogSource | null,
  actionsCatalog: RunnerRegistration['actionCatalog'],
  getConnectionId: () => string | null,
  processGeneration: string,
  opencodeCatalog: OpencodeModelCatalog,
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
      // OpenCode discovery assists configuration only. Execution still lets
      // OpenCode validate the operator-selected model and variant. The Pi
      // entry is published only once its catalog has actually loaded.
      opencode: {
        models: [...opencodeCatalog.models],
        variants: Object.fromEntries(
          Object.entries(opencodeCatalog.variants).map(([model, variants]) => [model, [...variants]]),
        ),
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
