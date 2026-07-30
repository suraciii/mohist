import { useQuery } from '@tanstack/react-query'
import { useProject } from '../../project/@x/project-context'
import { getConnectionDiagnostic } from './client'

export function connectionDiagnosticQueryOptions(
  projectId: string | null | undefined,
  connectionId: string | null | undefined,
) {
  return {
    queryKey: ['agent-connection-diagnostic', projectId, connectionId],
    queryFn: () => getConnectionDiagnostic(projectId, connectionId!),
    enabled: !!projectId && !!connectionId,
  }
}

export function useConnectionDiagnostic(connectionId: string | null | undefined) {
  const { projectId } = useProject()
  return useQuery(connectionDiagnosticQueryOptions(projectId, connectionId))
}
