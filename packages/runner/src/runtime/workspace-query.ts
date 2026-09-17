import { isAbsolute, relative, resolve } from 'node:path'
import { namedWorkspacePath } from './workspace-entity.js'
import { repositoryWorkspacePath } from './workspace-managed.js'

// Wire shape for workspace-scoped control WebSocket queries. The only
// disk-backed identity is the Named Workspace `(projectId, workspaceName)`;
// the runner derives the directory from `namedWorkspacePath(runnerRoot, ...)`
// and validates the on-disk marker. `repositoryName` selects the
// `REPOS/<repository>` checkout, `gitUrl` is the expected origin, `branch` is
// the Named Workspace head ref (`mohist/ws-<workspaceName>`) and `baseBranch`
// is the upstream ref the server-side review APIs diff against. `issueNumber`
// is preserved for log/telemetry only — it is NOT used to derive a head ref.
export interface WorkspaceQuery {
  projectId?: string | null
  workspaceName?: string | null
  issueNumber?: number | null
  repositoryName?: string | null
  gitUrl?: string | null
  branch?: string | null
  baseBranch?: string | null
}

export interface CompleteWorkspaceIdentity extends WorkspaceQuery {
  projectId: string
  workspaceName: string
  repositoryName: string
  gitUrl: string
  branch: string
  baseBranch: string
}

export interface ResolvedWorkspaceQuery {
  workspacePath: string
  workDir: string
  baseBranch: string
  head: string
  identity: CompleteWorkspaceIdentity
}

// Resolve a workspace query into the triple every git-backed handler needs,
// deriving the directory from the Named Workspace identity. Returns `null`
// when the query does not carry a complete named identity or when the runner
// root is unavailable — the server-side review APIs surface that as
// `branch_missing` / `workspace_identity_mismatch` rather than the handler
// falling through to a phantom ref. The resolver MUST NOT accept a
// Server-supplied path as identity.
export function resolveWorkspaceQuery(
  query: WorkspaceQuery | null | undefined,
  runnerRoot: string | null | undefined,
): ResolvedWorkspaceQuery | null {
  if (!runnerRoot || !hasCompleteWorkspaceIdentity(query)) return null
  const workspacePath = namedWorkspacePath(runnerRoot, query.projectId, query.workspaceName)
  const workDir = repositoryWorkspacePath(workspacePath, query.repositoryName)
  return {
    workspacePath,
    workDir,
    baseBranch: query.baseBranch,
    head: query.branch,
    identity: query,
  }
}

export function hasCompleteWorkspaceIdentity(
  query: WorkspaceQuery | null | undefined,
): query is CompleteWorkspaceIdentity {
  return Boolean(
    query?.projectId && query.workspaceName && query.repositoryName && query.gitUrl && query.branch && query.baseBranch,
  )
}

// Containment check: is `candidate` nested strictly under the runner root
// (no `..` traversal, no absolute leak)? Used
// by the cleanup loop to refuse deleting workspaces outside the runner
// root, and by the manual `RemoveWorkspace` handler for the same guard.
export function isUnderRunnerRoot(root: string, candidate: string): boolean {
  const rootPath = resolve(root)
  const target = resolve(candidate)
  const rel = relative(rootPath, target)
  return rel !== '' && !rel.startsWith('..') && !isAbsolute(rel)
}
