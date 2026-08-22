// Workspace materialization reporting extracted from ServerConnection to
// keep the main module within the file-size ratchet. The transport is the
// connection's authenticated fetch surface; the behavior is unchanged.
import { WorkspaceHomeClaimedError } from '../runtime/workspace-entity.js'
import type { WorkspaceMaterializedReport, WorkspaceReclaimability } from './connection.js'

export interface WorkspaceReportTransport {
  fetchWithAuth(input: string, init: RequestInit): Promise<Response>
  url(path: string): string
}

export async function reportWorkspaceMaterialized(
  transport: WorkspaceReportTransport,
  projectId: string,
  workspaceName: string,
  path: string,
  signal: AbortSignal,
): Promise<WorkspaceMaterializedReport> {
  const response = await transport.fetchWithAuth(
    transport.url(`workspaces/${encodeURIComponent(projectId)}/${encodeURIComponent(workspaceName)}/materialized`),
    { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ path }), signal },
  )
  if (!response.ok) {
    const text = await response.text()
    let code: string | null = null
    try {
      const payload = JSON.parse(text) as unknown
      if (payload && typeof payload === 'object') {
        const candidate = (payload as { code?: unknown }).code
        if (typeof candidate === 'string') code = candidate
      }
    } catch {
      // non-JSON error body; the status still explains the failure
    }
    if (code === 'workspace_home_claimed') {
      throw new WorkspaceHomeClaimedError(
        `workspace materialization rejected: workspace is already materialized on another runner (${response.status})`,
      )
    }
    throw new Error(`workspace materialization failed: ${response.status} ${text}`)
  }
  return response.json() as Promise<WorkspaceMaterializedReport>
}

export async function getWorkspaceReclaimability(
  transport: WorkspaceReportTransport,
  parse: (payload: unknown) => WorkspaceReclaimability,
  projectId: string,
  workspaceName: string,
  signal: AbortSignal,
): Promise<WorkspaceReclaimability> {
  const response = await transport.fetchWithAuth(
    transport.url(`workspaces/${encodeURIComponent(projectId)}/${encodeURIComponent(workspaceName)}/reclaimable`),
    { method: 'GET', signal },
  )
  if (!response.ok) throw new Error(`workspace reclaimability failed: ${response.status} ${await response.text()}`)
  let payload: unknown
  try {
    payload = await response.json()
  } catch {
    throw new Error('workspace reclaimability returned malformed JSON')
  }
  return parse(payload)
}
