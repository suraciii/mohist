import { getSegments } from '../core/json-path.js'
import { createRunnerProtocolError, type RunnerRequestTransport } from './connection-transport.js'

export interface WorkspaceArtifactInfo {
  readonly artifactId: string
  readonly path: string
  readonly kind: 'file' | 'directory'
  readonly contentType: string | null
  readonly contentHash: string | null
  readonly size: number | null
}

export interface WorkspaceArtifactDirectoryEntry {
  readonly relativePath: string
  readonly size: number
  readonly contentHash: string | null
  readonly contentType: string | null
}

export interface WorkspaceArtifactDirectory {
  readonly artifactId: string
  readonly path: string
  readonly entries: readonly WorkspaceArtifactDirectoryEntry[]
  readonly totalSize: number
}

export interface WorkspaceArtifactTransport {
  request: RunnerRequestTransport['request']
  readJson: RunnerRequestTransport['readJson']
  readBytes: RunnerRequestTransport['readBytes']
  url(path: string): string
}

export async function listWorkspaceArtifacts(
  transport: WorkspaceArtifactTransport,
  workflowRunId: string,
  workId: string,
  signal: AbortSignal,
): Promise<WorkspaceArtifactInfo[]> {
  const response = await transport.request(
    'listWorkspaceArtifacts',
    transport.url(
      `workflow-runs/${encodeURIComponent(workflowRunId)}/work/${encodeURIComponent(workId)}/workspace-artifacts`,
    ),
    { method: 'GET', signal },
  )
  const payload = await transport.readJson<unknown>(response, 'listWorkspaceArtifacts')
  const data = readObject(payload, ['data'])
  const artifacts = data?.artifacts
  if (!Array.isArray(artifacts)) {
    throw createRunnerProtocolError('listWorkspaceArtifacts', 'returned a malformed artifact list')
  }

  return artifacts.map((artifact, index) => parseArtifact(artifact, index))
}

export async function readWorkspaceArtifactDirectory(
  transport: WorkspaceArtifactTransport,
  workflowRunId: string,
  workId: string,
  artifactId: string,
  signal: AbortSignal,
): Promise<WorkspaceArtifactDirectory> {
  const response = await transport.request(
    'readWorkspaceArtifactDirectory',
    artifactContentUrl(transport, workflowRunId, workId, artifactId),
    { method: 'GET', signal },
  )
  const payload = await transport.readJson<unknown>(response, 'readWorkspaceArtifactDirectory')
  const data = readObject(payload, ['data'])
  if (!data) throw createRunnerProtocolError('readWorkspaceArtifactDirectory', 'returned a malformed directory')

  const artifactIdValue = readString(data, ['artifactId'])
  const path = readString(data, ['path'])
  const totalSize = readNumber(data, ['totalSize'])
  const entries = data.entries
  if (!artifactIdValue || !path || totalSize === null || !Array.isArray(entries)) {
    throw createRunnerProtocolError('readWorkspaceArtifactDirectory', 'returned a malformed directory')
  }

  return {
    artifactId: artifactIdValue,
    path,
    totalSize,
    entries: entries.map((entry, index) => {
      const value = asRecord(entry)
      const relativePath = value ? readString(value, ['relativePath']) : null
      const size = value ? readNumber(value, ['size']) : null
      if (!relativePath || size === null || size < 0) {
        throw createRunnerProtocolError(
          'readWorkspaceArtifactDirectory',
          `returned a malformed directory entry at index ${index}`,
        )
      }
      return {
        relativePath,
        size,
        contentHash: value ? readNullableString(value, ['contentHash']) : null,
        contentType: value ? readNullableString(value, ['contentType']) : null,
      }
    }),
  }
}

export async function downloadWorkspaceArtifact(
  transport: WorkspaceArtifactTransport,
  workflowRunId: string,
  workId: string,
  artifactId: string,
  signal: AbortSignal,
  file?: string,
): Promise<Uint8Array> {
  const url = new URL(artifactContentUrl(transport, workflowRunId, workId, artifactId))
  if (file) url.searchParams.set('file', file)
  const response = await transport.request('downloadWorkspaceArtifact', url.toString(), {
    method: 'GET',
    signal,
  })
  return await transport.readBytes(response, 'downloadWorkspaceArtifact')
}

function artifactContentUrl(
  transport: WorkspaceArtifactTransport,
  workflowRunId: string,
  workId: string,
  artifactId: string,
): string {
  return transport.url(
    `workflow-runs/${encodeURIComponent(workflowRunId)}/work/${encodeURIComponent(workId)}/workspace-artifacts/${encodeURIComponent(artifactId)}/content`,
  )
}

function parseArtifact(value: unknown, index: number): WorkspaceArtifactInfo {
  const artifact = asRecord(value)
  const artifactId = artifact ? readString(artifact, ['artifactId']) : null
  const path = artifact ? readString(artifact, ['path']) : null
  const kind = artifact ? readString(artifact, ['kind']) : null
  if (!artifactId || !path || (kind !== 'file' && kind !== 'directory')) {
    throw createRunnerProtocolError('listWorkspaceArtifacts', `returned a malformed artifact at index ${index}`)
  }
  const size = artifact ? readNullableNumber(artifact, ['size']) : null
  if (size !== null && size < 0) {
    throw createRunnerProtocolError('listWorkspaceArtifacts', `returned an invalid artifact size at index ${index}`)
  }
  return {
    artifactId,
    path,
    kind,
    contentType: artifact ? readNullableString(artifact, ['contentType']) : null,
    contentHash: artifact ? readNullableString(artifact, ['contentHash']) : null,
    size,
  }
}

function readObject(value: unknown, path: string[]): Record<string, unknown> | null {
  return asRecord(getSegments(value, path))
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null
}

function readString(value: Record<string, unknown>, path: string[]): string | null {
  const found = getSegments(value, path)
  return typeof found === 'string' && found.length > 0 ? found : null
}

function readNullableString(value: Record<string, unknown>, path: string[]): string | null {
  const found = getSegments(value, path)
  return found === null || found === undefined ? null : typeof found === 'string' ? found : null
}

function readNumber(value: Record<string, unknown>, path: string[]): number | null {
  const found = getSegments(value, path)
  return typeof found === 'number' && Number.isFinite(found) ? found : null
}

function readNullableNumber(value: Record<string, unknown>, path: string[]): number | null {
  const found = getSegments(value, path)
  return found === null || found === undefined ? null : readNumber(value, path)
}
