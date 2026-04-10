import type { ApiResponse } from './types'

export interface Provider {
  id: string
  name: string
  baseURL: string | null
  models: string[]
  configured: boolean
  source: 'config' | 'env' | 'none'
  isBuiltin: boolean
  isDefault: boolean
  apiKeyMasked: string | null
}

export interface ProviderFormData {
  name?: string
  apiKey: string
  baseURL?: string
  models?: string[]
  sdk?: string
}

const BASE = '/api'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })
  const json: ApiResponse<T> = await res.json()
  if (!json.success) {
    throw new Error(json.error ?? `Request failed: ${res.status}`)
  }
  return json.data as T
}

export const providerApi = {
  getProviders: () =>
    request<Provider[]>('/providers'),

  saveProvider: (id: string, data: ProviderFormData) =>
    request<{ id: string; configured: boolean }>(`/providers/${encodeURIComponent(id)}`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  deleteProvider: (id: string) =>
    request<{ id: string }>(`/providers/${encodeURIComponent(id)}`, {
      method: 'DELETE',
    }),

  testProvider: (data: ProviderFormData & { id?: string }) =>
    request<{ success: boolean }>('/providers/test', {
      method: 'POST',
      body: JSON.stringify(data),
    }),
}