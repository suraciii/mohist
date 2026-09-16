import { ApiError, projectApiPath, request } from '../../../shared/api/client'
import type {
  ActionCatalog,
  AgentRuntime,
  AgentRuntimeConfig,
  GeneralConfig,
  RuntimeConsistencyResponse,
  SystemInfo,
  SystemUpdateStatusEnvelope,
  WorkflowProfileDetail,
} from '../model/types'

export const AGENT_RUNTIME_OPENCODE = 'opencode'
export const AGENT_RUNTIME_PI = 'pi'

export const AGENT_RUNTIMES = [AGENT_RUNTIME_OPENCODE, AGENT_RUNTIME_PI] as const

export type { AgentRuntime } from '../model/types'

export function isAgentRuntime(value: string | null | undefined): value is AgentRuntime {
  return value === AGENT_RUNTIME_OPENCODE || value === AGENT_RUNTIME_PI
}

export const DEFAULT_AGENT_RUNTIME: AgentRuntime = AGENT_RUNTIME_PI

export type OpencodeModelVariants = Record<string, string[]>

export function getConfig() {
  return request<GeneralConfig>('/config')
}

export function updateConfig(key: string, value: number | string) {
  return request<GeneralConfig>(`/config/${encodeURIComponent(key)}`, {
    method: 'PUT',
    body: JSON.stringify({ value }),
  })
}

export function getLogLevel() {
  return getConfig().then((config) => ({ level: config.logLevel ?? 'INFO' }))
}

export function setLogLevel(level: string) {
  return updateConfig('logLevel', level).then((config) => ({ level: config.logLevel ?? level }))
}

export function getModels(
  projectId: string | null | undefined,
  runtime: AgentRuntime | string = DEFAULT_AGENT_RUNTIME,
) {
  const query = `?runtime=${encodeURIComponent(runtime)}`
  return request<{ models: string[]; modelVariants?: OpencodeModelVariants; reasoningEfforts?: OpencodeModelVariants }>(
    projectApiPath(projectId, `/opencode/models${query}`),
  )
}

export function getOpencodeModelVariantsFor(
  modelIds: ReadonlyArray<string | null | undefined>,
  variantsMap?: OpencodeModelVariants | null,
): OpencodeModelVariants {
  const result: OpencodeModelVariants = {}
  if (!variantsMap) return result
  for (const id of modelIds) {
    if (!id) continue
    const variants = variantsMap[id]
    if (variants && variants.length > 0) result[id] = variants
  }
  return result
}

export const SUPPORTED_RUNTIME_KEYS = [
  'maxConcurrentAgents',
  'agentTimeout',
  'taskTimeout',
  'stageTimeout',
  'pollInterval',
  'maxGracePeriods',
] as const

export type SupportedRuntimeKey = (typeof SUPPORTED_RUNTIME_KEYS)[number]

const RUNTIME_KEY_TO_CONFIG_KEY: Record<keyof AgentRuntimeConfig, SupportedRuntimeKey> = {
  timeout: 'agentTimeout',
  taskTimeout: 'taskTimeout',
  stageTimeout: 'stageTimeout',
  maxConcurrent: 'maxConcurrentAgents',
  maxGracePeriods: 'maxGracePeriods',
  pollInterval: 'pollInterval',
}

export function configToAgentRuntime(config: GeneralConfig | null | undefined): AgentRuntimeConfig {
  return {
    timeout: secondsToMs(toNumber(config?.agentTimeout)),
    taskTimeout: secondsToMs(toNumber(config?.taskTimeout)),
    stageTimeout: secondsToMs(toNumber(config?.stageTimeout)),
    maxConcurrent: toNumber(config?.maxConcurrentAgents),
    maxGracePeriods: toNumber(config?.maxGracePeriods),
    pollInterval: toNumber(config?.pollInterval),
  }
}

export function agentRuntimeToConfigKey(key: keyof AgentRuntimeConfig): SupportedRuntimeKey {
  return RUNTIME_KEY_TO_CONFIG_KEY[key]
}

export function getAgentRuntime() {
  return getConfig().then((config) => configToAgentRuntime(config))
}

export function updateAgentRuntime(data: Partial<AgentRuntimeConfig>) {
  const writes: Array<Promise<GeneralConfig>> = []
  const unsupported: Array<keyof AgentRuntimeConfig> = []

  for (const key of Object.keys(data) as Array<keyof AgentRuntimeConfig>) {
    const configKey = RUNTIME_KEY_TO_CONFIG_KEY[key]
    if (!configKey) {
      unsupported.push(key)
      continue
    }
    const rawValue = data[key]
    if (rawValue === undefined) continue
    const value = encodeRuntimeValue(key, rawValue)
    writes.push(updateConfig(configKey, value))
  }

  if (writes.length === 0) {
    if (unsupported.length > 0) {
      const message = `Runtime field(s) not supported: ${unsupported.join(', ')}`
      return Promise.reject(new ApiError(message, 400))
    }
    return getConfig().then((config) => configToAgentRuntime(config))
  }

  return Promise.all(writes).then((results) => {
    const last = results[results.length - 1]
    if (unsupported.length > 0) {
      const message = `Runtime field(s) not supported: ${unsupported.join(', ')}`
      throw new ApiError(message, 400, last, 'unsupported_field')
    }
    return configToAgentRuntime(last)
  })
}

function toNumber(value: unknown): number {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const n = Number(value)
    return Number.isFinite(n) ? n : 0
  }
  return 0
}

function secondsToMs(seconds: number): number {
  return Math.round(seconds * 1000)
}

function encodeRuntimeValue(key: keyof AgentRuntimeConfig, value: number): number {
  if (key === 'timeout' || key === 'taskTimeout' || key === 'stageTimeout') {
    return Math.round(value / 1000)
  }
  return value
}

export function getWorkflowProfiles(projectId: string) {
  return request<WorkflowProfileCollectionEntryResponse[]>(projectApiPath(projectId, '/workflow-profiles')).then(
    (profiles) => profiles.map(mapWorkflowProfileInfo),
  )
}

interface WorkflowProfileCollectionEntryResponse {
  projectId: string
  profileId: string
  name: string
  description: string
  sourceProvenance: string
  isBuiltIn: boolean
  definitionSource: string | null
}

interface WorkflowProfileDetailResponse extends WorkflowProfileCollectionEntryResponse {
  stages: Array<{
    stage: string
    requiresApproval: boolean
    tasks: string[]
    checks: string[]
  }>
}

function mapWorkflowProfileInfo(profile: WorkflowProfileCollectionEntryResponse) {
  return {
    id: profile.profileId,
    displayName: profile.name,
    description: profile.description,
    isDefault: profile.profileId === 'mohist/local',
    isBuiltIn: profile.isBuiltIn,
  }
}

function mapWorkflowProfileDetail(profile: WorkflowProfileDetailResponse): WorkflowProfileDetail {
  return {
    ...mapWorkflowProfileInfo(profile),
    projectId: profile.projectId,
    sourceProvenance: profile.sourceProvenance,
    isBuiltIn: profile.isBuiltIn,
    definitionSource: profile.definitionSource,
    yaml: profile.definitionSource ?? '',
    stages: profile.stages,
  }
}

export function getWorkflowProfile(projectId: string, id: string, requester: typeof request = request) {
  return requester<WorkflowProfileDetailResponse>(projectApiPath(projectId, `/workflow-profiles/${id}`)).then(
    mapWorkflowProfileDetail,
  )
}

export function getActionCatalog(projectId: string) {
  return request<ActionCatalog>(projectApiPath(projectId, '/actions'))
}

export interface ProjectDefaultWorkflowProfile {
  projectId: string
  defaultTemplateId: string | null
  disabledWorkflowProfileIds: string[]
}

export function getProjectDefaultWorkflowProfile(projectId?: string | null) {
  return request<{ projectId: string; defaultWorkflowProfileId: string | null; disabledWorkflowProfileIds?: string[] }>(
    projectApiPath(projectId, '/workflow-profile/default'),
  ).then((response) => ({
    projectId: response.projectId,
    defaultTemplateId: response.defaultWorkflowProfileId ?? null,
    disabledWorkflowProfileIds: response.disabledWorkflowProfileIds ?? [],
  }))
}

export function setProjectDefaultWorkflowProfile(projectId: string | null | undefined, templateId: string) {
  return request<{ projectId: string; profileId: string }>(projectApiPath(projectId, '/workflow-profile/default'), {
    method: 'PUT',
    body: JSON.stringify({ profileId: templateId }),
  })
}

export function disableWorkflowProfile(projectId: string | null | undefined, profileId: string) {
  return request<void>(projectApiPath(projectId, '/workflow-profile/disable'), {
    method: 'POST',
    body: JSON.stringify({ profileId }),
  })
}

export function enableWorkflowProfile(projectId: string | null | undefined, profileId: string) {
  return request<void>(projectApiPath(projectId, '/workflow-profile/enable'), {
    method: 'POST',
    body: JSON.stringify({ profileId }),
  })
}

export function getSystemInfo() {
  return request<SystemInfo>('/system/info')
}

export function getSystemUpdateStatus() {
  return request<SystemUpdateStatusEnvelope>('/system/update/status')
}

export function getRuntimeConsistency() {
  return request<RuntimeConsistencyResponse>('/system/consistency')
}
