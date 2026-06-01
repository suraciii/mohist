import { request, withProject } from '../../../shared/api/client'
import type { RunnerStatusListResponse } from '../model/types'

export function getRunners(projectId: string | null) {
  return request<RunnerStatusListResponse>('/runners', withProject(undefined, projectId))
}