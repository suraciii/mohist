import { ApiError, projectApiPath, request } from '../../../shared/api/client'
import type {
  ActionCatalog,
  AgentRuntime,
  AgentRuntimeConfig,
  GeneralConfig,
  RuntimeConsistencyResponse,
  SystemInfo,
  SystemUpdateStartResponse,
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

export const DEFAULT_AGENT_RUNTIME: AgentRuntime = AGENT_RUNTIME_OPENCODE

export interface VariableBundle {
  vars?: Record<string, unknown> | null
  stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null
}

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

export type OpencodeModelVariants = Record<string, string[]>

export function getOpencodeModels(projectId?: string | null) {
  return request<{ models: string[]; modelVariants?: OpencodeModelVariants }>(
    projectApiPath(projectId, '/opencode/models'),
  )
}

export function getModels(
  projectId: string | null | undefined,
  runtime: AgentRuntime | string = DEFAULT_AGENT_RUNTIME,
) {
  const query = `?runtime=${encodeURIComponent(runtime)}`
  return request<{ models: string[]; modelVariants?: OpencodeModelVariants }>(
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

export function getProjectWorkflowVariables(projectId?: string | null) {
  return request<VariableBundle>(projectApiPath(projectId, '/variables'))
}

export function patchProjectWorkflowVariables(projectId: string | null | undefined, patch: VariableBundle) {
  return request<VariableBundle>(projectApiPath(projectId, '/variables'), {
    method: 'PATCH',
    body: JSON.stringify(patch),
  })
}

export function getOpencodeModel(projectId?: string | null) {
  return getProjectWorkflowVariables(projectId).then((variables) => ({
    model: getAgentModel(variables.vars),
    variant: getAgentVariant(variables.vars),
  }))
}

export function updateOpencodeModel(
  projectId: string | null | undefined,
  model: string | null,
  variant?: string | null,
) {
  const agent: Record<string, unknown> = { model }
  if (variant !== undefined) agent.variant = variant
  return patchProjectWorkflowVariables(projectId, { vars: { agent } }).then((variables) => ({
    model: getAgentModel(variables.vars),
    variant: getAgentVariant(variables.vars),
  }))
}

export function getModel() {
  return request<{ model: string | null }>('/model')
}

export function setModel(model: string | null) {
  return request<{ model: string | null }>('/model', {
    method: 'PUT',
    body: JSON.stringify({ model }),
  })
}

export function getOpencodeModelConfig() {
  return request<{ model: string | null }>('/opencode-model')
}

export function setOpencodeModel(model: string | null) {
  return request<{ model: string | null }>('/opencode-model', {
    method: 'PUT',
    body: JSON.stringify({ model }),
  })
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

export function getOpencodeRuntime() {
  return request<{ mode: string; command: string; model: string | null; note: string }>('/opencode/runtime')
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

export function getStageModels(projectId?: string | null) {
  return getProjectWorkflowVariables(projectId).then((variables) => ({
    stageModels: getStageModelMap(variables),
    stageModelVariants: getStageModelVariantMap(variables),
  }))
}

export function setStageModel(
  projectId: string | null | undefined,
  stage: string,
  model: string | null,
  variant?: string | null,
) {
  const agent: Record<string, unknown> = { model }
  if (variant !== undefined) agent.variant = variant
  return patchProjectWorkflowVariables(projectId, { stages: { [stage]: { vars: { agent } } } }).then((variables) => ({
    stageModels: getStageModelMap(variables),
    stageModelVariants: getStageModelVariantMap(variables),
  }))
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
  agentAction?: string | null
  agentRuntime?: AgentRuntime | null
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
    agentAction: profile.agentAction ?? null,
    agentRuntime: profile.agentRuntime ?? null,
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

export function patchWorkflowProfileAgentAction(projectId: string, profileId: string, agentAction: string | null) {
  return request<WorkflowProfileCollectionEntryResponse>(projectApiPath(projectId, `/workflow-profiles/${profileId}`), {
    method: 'PATCH',
    body: JSON.stringify({ agentAction }),
  }).then(mapWorkflowProfileInfo)
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

export function startSystemUpdate() {
  return request<SystemUpdateStartResponse>('/system/update', {
    method: 'POST',
    body: JSON.stringify({}),
  })
}

export function getSystemUpdateStatus() {
  return request<SystemUpdateStatusEnvelope>('/system/update/status')
}

export function getRuntimeConsistency() {
  return request<RuntimeConsistencyResponse>('/system/consistency')
}

function getAgentModel(vars: Record<string, unknown> | null | undefined) {
  const agent = vars?.agent
  if (!agent || typeof agent !== 'object') return null
  const model = (agent as Record<string, unknown>).model
  return typeof model === 'string' && model.trim() ? model : null
}

function getAgentVariant(vars: Record<string, unknown> | null | undefined) {
  const agent = vars?.agent
  if (!agent || typeof agent !== 'object') return null
  const variant = (agent as Record<string, unknown>).variant
  return typeof variant === 'string' && variant.trim() ? variant : null
}

function getStageModelMap(variables: VariableBundle) {
  const entries = Object.entries(variables.stages ?? {})
    .map(([stage, stageVars]) => [stage, getAgentModel(stageVars?.vars)] as const)
    .filter((entry): entry is readonly [string, string] => typeof entry[1] === 'string')

  return entries.length > 0 ? Object.fromEntries(entries) : null
}

function getStageModelVariantMap(variables: VariableBundle) {
  const entries = Object.entries(variables.stages ?? {})
    .map(([stage, stageVars]) => [stage, getAgentVariant(stageVars?.vars)] as const)
    .filter((entry): entry is readonly [string, string] => typeof entry[1] === 'string')

  return entries.length > 0 ? Object.fromEntries(entries) : null
}
