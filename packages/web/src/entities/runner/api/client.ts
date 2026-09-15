import { request } from '../../../shared/api/client'
import type { RunnerStatusDetailResponse, RunnerStatusListResponse } from '../model/types'

export function getRunners() {
  return request<RunnerStatusListResponse>('/runners')
}

export function getRunner(runnerId: string) {
  return request<RunnerStatusDetailResponse>(`/runners/${encodeURIComponent(runnerId)}`)
}

export function updateRunnerSlots(runnerId: string, slots: number): Promise<{ runnerId: string; slots: number }> {
  return request(`/runner/${encodeURIComponent(runnerId)}`, {
    method: 'PATCH',
    body: JSON.stringify({ slots }),
  })
}
