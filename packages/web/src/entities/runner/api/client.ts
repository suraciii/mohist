import { request, projectApiPath } from '../../../shared/api/client'
import type { RunnerStatusListResponse } from '../model/types'

export function getRunners(projectId: string | null) {
  return request<RunnerStatusListResponse>(projectApiPath(projectId, '/runners'))
}
