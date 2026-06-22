import { request, projectApiPath } from '../../../shared/api/client'
import type { RunnerStatusDetailResponse, RunnerStatusListResponse } from '../model/types'

export function getRunners(projectId: string | null) {
  return request<RunnerStatusListResponse>(projectApiPath(projectId, '/runners'))
}

export function getRunner(projectId: string | null, runnerId: string) {
  return request<RunnerStatusDetailResponse>(
    projectApiPath(projectId, `/runners/${encodeURIComponent(runnerId)}`),
  )
}

export function updateRunnerSlots(
  runnerId: string,
  slots: number,
): Promise<{ runnerId: string; slots: number }> {
  return request(`/runner/${encodeURIComponent(runnerId)}`, {
    method: 'PATCH',
    body: JSON.stringify({ slots }),
  })
}
