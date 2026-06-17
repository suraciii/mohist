import { ApiError, projectApiPath, request } from '../../../shared/api/client'
import type { AgentRuntimeConfig, GeneralConfig, RuntimeConsistencyResponse, SystemInfo, SystemUpdateStartResponse, SystemUpdateStatusEnvelope, WorkflowProfileDetail } from '../model/types'

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

export function getOpencodeModels(projectId?: string | null) {
  return request<{ models: string[] }>(projectApiPath(projectId, '/opencode/models'))
}

export function getProjectWorkflowVariables(projectId?: string | null) {
  return request<VariableBundle>(projectApiPath(projectId, '/workflow-profile/variables'))
}

export function patchProjectWorkflowVariables(projectId: string | null | undefined, patch: VariableBundle) {
  return request<VariableBundle>(projectApiPath(projectId, '/workflow-profile/variables'), {
    method: 'PATCH',
    body: JSON.stringify(patch),
  })
}

export function getOpencodeModel(projectId?: string | null) {
  return getProjectWorkflowVariables(projectId).then((variables) => ({ model: getAgentModel(variables.vars) }))
}

export function updateOpencodeModel(projectId: string | null | undefined, model: string | null) {
  return patchProjectWorkflowVariables(projectId, { vars: { agent: { type: 'opencode', model } } })
    .then((variables) => ({ model: getAgentModel(variables.vars) }))
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

export type SupportedRuntimeKey = typeof SUPPORTED_RUNTIME_KEYS[number]

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
  return getProjectWorkflowVariables(projectId).then((variables) => ({ stageModels: getStageModelMap(variables) }))
}

export function setStageModel(projectId: string | null | undefined, stage: string, model: string | null) {
  return patchProjectWorkflowVariables(projectId, { stages: { [stage]: { vars: { agent: { type: 'opencode', model } } } } })
    .then((variables) => ({ stageModels: getStageModelMap(variables) }))
}

export function getWorkflowProfiles() {
  return request<Array<{ id: string; name: string; description: string; isDefault: boolean }>>('/workflow-templates/system')
    .then((templates) => templates.map((template) => ({
      id: template.id,
      displayName: template.name,
      description: template.description,
      isDefault: template.isDefault,
    })))
}

export function getWorkflowProfile(id: string) {
  return request<WorkflowProfileDetail>(`/workflow-templates/system/${id}`)
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

function getStageModelMap(variables: VariableBundle) {
  const entries = Object.entries(variables.stages ?? {})
    .map(([stage, stageVars]) => [stage, getAgentModel(stageVars?.vars)] as const)
    .filter((entry): entry is readonly [string, string] => typeof entry[1] === 'string')

  return entries.length > 0 ? Object.fromEntries(entries) : null
}
