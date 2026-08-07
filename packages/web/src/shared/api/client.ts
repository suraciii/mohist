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

let unauthorizedListener: (() => void) | null = null

/**
 * Registers the app-level reaction to an unexpected 401: the auth surface
 * (login/probe/logout) drives its own state and is deliberately excluded.
 */
export function setUnauthorizedListener(listener: (() => void) | null) {
  unauthorizedListener = listener
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers)
  if (!headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers,
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
    if (res.status === 401 && !path.startsWith('/auth/')) {
      unauthorizedListener?.()
    }
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

export function projectApiPath(projectRef: string | null | undefined, path: string) {
  if (!projectRef) throw new ApiError('Project is required', 400)
  return `/projects/${encodeURIComponent(projectRef)}${path.startsWith('/') ? path : `/${path}`}`
}

export { ApiError }
