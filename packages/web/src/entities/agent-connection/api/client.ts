import { projectApiPath, request } from '@/shared/api/client'
import type { ConnectionDiagnostic } from '../model/types'

export function getConnectionDiagnostic(projectId: string | null | undefined, connectionId: string) {
  return request<ConnectionDiagnostic>(
    projectApiPath(projectId, `/slack-connections/${encodeURIComponent(connectionId)}/diagnostic`),
  )
}
