// Workspace materialization reporting extracted from ServerConnection to
// keep the main module within the file-size ratchet. The transport is the
// connection's authenticated fetch surface; the behavior is unchanged.
import { getSegments } from '../core/json-path.js'
import { WorkspaceHomeClaimedError } from '../runtime/workspace-entity.js'

/**
 * Answer shape for
 * `POST /api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/materialized`.
 * `runnerId` is the workspace home runner recorded by the server (this runner on success).
 */
export interface WorkspaceMaterializedReport {
  readonly runnerId: string
  readonly path: string
}

/**
 * Answer shape for
 * `GET /api/runner/{runnerId}/workspaces/{projectId}/{workspaceName}/reclaimable`.
 * `status` is the Workspace lifecycle status; `activeBoundSessions` counts
 * sessions bound to and actively using the workspace.
 */
export interface WorkspaceReclaimability {
  readonly status: 'active' | 'archived'
  readonly activeBoundSessions: number
}

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
  return parseWorkspaceReclaimability(readObject(payload, ['data']))
}

export function parseWorkspaceReclaimability(payload: unknown): WorkspaceReclaimability {
  if (!isObjectRecord(payload)) throw new Error('workspace reclaimability returned a malformed response')
  const status = readString(payload, ['status'])
  if (status !== 'active' && status !== 'archived') {
    throw new Error('workspace reclaimability returned an unknown status')
  }
  const count = readNumber(payload, ['activeBoundSessions'])
  if (count === null || !Number.isInteger(count) || count < 0) {
    throw new Error('workspace reclaimability returned an invalid session count')
  }
  return { status, activeBoundSessions: count }
}

function readObject(value: unknown, path: string[]): Record<string, unknown> | null {
  const found = getSegments(value, path)
  return isObjectRecord(found) ? found : null
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function readString(value: unknown, path: string[]): string | null {
  const found = getSegments(value, path)
  return typeof found === 'string' ? found : null
}

function readNumber(value: unknown, path: string[]): number | null {
  const found = getSegments(value, path)
  return typeof found === 'number' && Number.isFinite(found) ? found : null
}
