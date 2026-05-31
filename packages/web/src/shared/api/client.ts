import type { ApiResponse } from './types'

const BASE = '/api'

class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly data?: unknown,
    public readonly code?: string,
    public readonly details?: unknown,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })
  const text = await res.text()
  let json: ApiResponse<T> | null = null

  if (text.trim()) {
    try {
      json = JSON.parse(text) as ApiResponse<T>
    } catch {
      throw new ApiError(`Invalid JSON response from ${path}`, res.status)
    }
  }

  if (!json) {
    throw new ApiError(`Empty response from ${path}`, res.status)
  }

  if (!json.success) {
    throw new ApiError(
      json.error ?? `Request failed: ${res.status}`,
      res.status,
      json.data,
      json.code,
      json.details,
    )
  }
  return json.data as T
}

export function withProject(path: string, projectId?: string | null): string {
  if (!projectId) return path
  const separator = path.includes('?') ? '&' : '?'
  return `${path}${separator}projectId=${encodeURIComponent(projectId)}`
}

export { ApiError }
