import { join, resolve } from 'node:path'
import { exists, readText } from '../system/process.js'
import type { WorkspaceBindingIdentity } from './workspace-registry.js'
import { WorkspaceIdentityMismatchError } from './workspace-errors.js'

export function issueWorkspacePath(runnerRoot: string, workflowRunId: string) {
  if (!/^wr[-_A-Za-z0-9]+$/.test(workflowRunId)) throw new WorkspaceIdentityMismatchError('Invalid workflow run id')
  return resolve(join(runnerRoot, 'workspaces', workflowRunId))
}

function runBranchName(runId: string | null | undefined) {
  const safe = (runId ?? '').replace(/[^A-Za-z0-9_-]/g, '')
  return safe ? `mohist/run-${safe}` : 'mohist/run'
}

export interface IssueWorkspaceMarker {
  workflowRunId: string
  runBranch: string
}

export function workspaceIdentity(workflowRunId: string): IssueWorkspaceMarker {
  return {
    workflowRunId,
    runBranch: runBranchName(workflowRunId),
  }
}

export function workspaceBindingIdentity(
  runnerRoot: string,
  runnerId: string,
  workflowRunId: string,
  gitUrl: string,
  baseBranch: string,
): WorkspaceBindingIdentity {
  return {
    runnerId,
    runnerRoot: resolve(runnerRoot),
    workflowRunId,
    gitUrl: gitUrl.trim(),
    baseBranch: baseBranch.trim(),
  }
}

export function markerPath(workspacePath: string) {
  return join(workspacePath, '.mohist', 'workspace.json')
}

// Read the workspace marker from disk. Returns `null` when the marker
// is missing or unreadable; the caller decides what kind of failure
// that is (corrupt vs missing). Used by both `verify()` (which needs
// to distinguish missing / corrupt / mismatch) and `planResolution()`
// (which just needs a yes/no answer).
export async function readMarker(workspacePath: string): Promise<Partial<IssueWorkspaceMarker> | null> {
  const path = markerPath(workspacePath)
  if (!exists(path)) return null
  try {
    const raw = await readText(path)
    return JSON.parse(raw) as Partial<IssueWorkspaceMarker>
  } catch {
    return null
  }
}

export async function readMarkerWorkflowRunId(workspacePath: string): Promise<string | null | undefined> {
  const marker = await readMarker(workspacePath)
  return marker?.workflowRunId
}
