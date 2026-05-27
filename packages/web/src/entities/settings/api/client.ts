import { request } from '../../../shared/api/client'
import type { AgentRuntimeConfig, GeneralConfig, SystemInfo } from '../model/types'

export function getConfig() {
  return request<GeneralConfig>('/config')
}

export function updateConfig(key: string, value: number) {
  return request<GeneralConfig>(`/config/${encodeURIComponent(key)}`, {
    method: 'PUT',
    body: JSON.stringify({ value }),
  })
}

export function getOpencodeModels() {
  return request<{ models: string[] }>('/opencode/models')
}

export function getOpencodeModel() {
  return request<{ model: string | null }>('/opencode-model')
}

export function updateOpencodeModel(model: string | null) {
  return request<{ model: string | null }>('/opencode-model', {
    method: 'PUT',
    body: JSON.stringify({ model }),
  })
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

export function getStageModels() {
  return request<{ stageModels: Record<string, string> | null }>('/stage-models')
}

export function setStageModels(stageModels: Record<string, string> | null) {
  return request<{ stageModels: Record<string, string> | null }>('/stage-models', {
    method: 'PUT',
    body: JSON.stringify({ stageModels }),
  })
}

export function getSystemInfo() {
  return request<SystemInfo>('/system/info')
}
