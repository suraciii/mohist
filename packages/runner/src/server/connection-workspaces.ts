// Workspace materialization reporting extracted from ServerConnection to
// keep the main module within the file-size ratchet. The transport is the
// connection's authenticated fetch surface; the behavior is unchanged.
import { getSegments } from '../core/json-path.js'
import { WorkspaceHomeClaimedError } from '../runtime/workspace-entity.js'
import { RunnerTransportError } from './connection-errors.js'
import { createRunnerProtocolError, type RunnerRequestTransport } from './connection-transport.js'

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
  request: RunnerRequestTransport['request']
  readJson: RunnerRequestTransport['readJson']
  url(path: string): string
}

export async function reportWorkspaceMaterialized(
  transport: WorkspaceReportTransport,
  projectId: string,
  workspaceName: string,
  path: string,
  signal: AbortSignal,
): Promise<WorkspaceMaterializedReport> {
  let response: Response
  try {
    response = await transport.request(
      'reportWorkspaceMaterialized',
      transport.url(`workspaces/${encodeURIComponent(projectId)}/${encodeURIComponent(workspaceName)}/materialized`),
      { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ path }), signal },
    )
  } catch (error) {
    if (
      error instanceof RunnerTransportError &&
      error.kind === 'http' &&
      error.serverCode === 'workspace_home_claimed'
    ) {
      throw new WorkspaceHomeClaimedError(
        `workspace materialization rejected: workspace is already materialized on another runner (${error.httpStatus ?? 'unknown status'})`,
      )
    }
    throw error
  }
  const payload = await transport.readJson<unknown>(response, 'reportWorkspaceMaterialized')
  const data = isSuccessfulApiResponse(payload) ? payload.data : null
  if (!isObjectRecord(data) || !nonEmptyString(data.runnerId) || !nonEmptyString(data.path)) {
    throw createRunnerProtocolError('reportWorkspaceMaterialized', 'returned a malformed response')
  }
  return { runnerId: data.runnerId, path: data.path }
}

export async function getWorkspaceReclaimability(
  transport: WorkspaceReportTransport,
  projectId: string,
  workspaceName: string,
  signal: AbortSignal,
): Promise<WorkspaceReclaimability> {
  const response = await transport.request(
    'getWorkspaceReclaimability',
    transport.url(`workspaces/${encodeURIComponent(projectId)}/${encodeURIComponent(workspaceName)}/reclaimable`),
    { method: 'GET', signal },
  )
  const payload = await transport.readJson<unknown>(response, 'getWorkspaceReclaimability')
  try {
    return parseWorkspaceReclaimability(readObject(payload, ['data']))
  } catch (cause) {
    const detail =
      cause instanceof Error ? cause.message.replace(/^workspace reclaimability /, '') : 'returned a malformed response'
    throw createRunnerProtocolError('getWorkspaceReclaimability', detail, cause)
  }
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

function isSuccessfulApiResponse(value: unknown): value is { success: true; data: unknown } {
  return isObjectRecord(value) && value.success === true && 'data' in value
}

function nonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function readString(value: unknown, path: string[]): string | null {
  const found = getSegments(value, path)
  return typeof found === 'string' ? found : null
}

function readNumber(value: unknown, path: string[]): number | null {
  const found = getSegments(value, path)
  return typeof found === 'number' && Number.isFinite(found) ? found : null
}
