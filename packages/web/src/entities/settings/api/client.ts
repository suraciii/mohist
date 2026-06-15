import { request, projectApiPath } from '../../../shared/api/client'
import type { AgentRuntimeConfig, GeneralConfig, SystemInfo, SystemUpdateStartResponse, SystemUpdateStatusEnvelope, WorkflowProfileDetail } from '../model/types'

export interface VariableBundle {
  vars?: Record<string, unknown> | null
  stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null
}

export function getConfig() {
  return request<GeneralConfig>('/config')
}

export function updateConfig(key: string, value: number) {
  return request<GeneralConfig>(`/config/${encodeURIComponent(key)}`, {
    method: 'PUT',
    body: JSON.stringify({ value }),
  })
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

export function getLogLevel() {
  return request<{ level: string }>('/log-level')
}

export function setLogLevel(level: string) {
  return request<{ level: string }>('/log-level', {
    method: 'PUT',
    body: JSON.stringify({ level }),
  })
}

export function getAgentRuntime() {
  return request<AgentRuntimeConfig>('/agent-runtime')
}

export function getOpencodeRuntime() {
  return request<{ mode: string; command: string; model: string | null; note: string }>('/opencode/runtime')
}

export function updateAgentRuntime(data: Partial<AgentRuntimeConfig>) {
  return request<AgentRuntimeConfig>('/agent-runtime', {
    method: 'PUT',
    body: JSON.stringify(data),
  })
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
  return request<WorkflowProfileDetail>(`/workflow-templates/system/${encodeURIComponent(id)}`)
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
