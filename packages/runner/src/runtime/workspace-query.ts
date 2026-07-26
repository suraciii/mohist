import { isAbsolute, relative, resolve } from "node:path"

// Wire shape for workspace-scoped SignalR queries. `workspacePath` is the
// on-disk worktree the runner materialized; `branch` is the head ref the
// dispatch put into the worktree (e.g. `mohist/run-${workflowRunId}`);
// `baseBranch` is the upstream branch the server-side review APIs diff
// against. `issueNumber` is preserved for log/telemetry only — it is NOT
// used to derive a head ref.
export interface WorkspaceQuery {
  workflowRunId?: string | null
  projectId?: string | null
  issueNumber?: number
  repositoryName?: string | null
  gitUrl?: string | null
  workspacePath?: string | null
  branch?: string | null
  baseBranch?: string | null
}

type ResolvedWorkspaceQuery =
  | { workDir: string; baseBranch: string; head: string }
  | { workDir: string; baseBranch: string; head: string; identity: WorkspaceQuery }

// Resolve a workspace query into the triple every git-backed handler
// needs. Returns `null` when ANY of `workspacePath` / `baseBranch` /
// `branch` is missing — the server-side review APIs surface that as
// `branch_missing` rather than the handler falling through to a phantom
// ref. The resolver MUST NOT synthesize a head ref from `issueNumber`:
// the runner only ever creates `mohist/run-${workflowRunId}` refs, so a
// synthesized ref would never resolve.
export function resolveWorkspaceQuery(
  query: WorkspaceQuery | null | undefined,
): ResolvedWorkspaceQuery | null {
  if (!query?.workspacePath || !query.baseBranch) return null
  const head = query.branch ?? null
  if (!head) return null
  const identityFields = [query.workflowRunId, query.gitUrl]
  const hasIdentity = identityFields.some((value) => value !== undefined && value !== null)
  if (hasIdentity && (!query.workflowRunId || !query.gitUrl)) return null
  const resolved = { workDir: query.workspacePath, baseBranch: query.baseBranch, head }
  return hasIdentity ? { ...resolved, identity: query } : resolved
}

export function hasCompleteWorkspaceIdentity(query: WorkspaceQuery | null | undefined): query is WorkspaceQuery & {
  workflowRunId: string
  gitUrl: string
  workspacePath: string
  branch: string
  baseBranch: string
} {
  const resolved = resolveWorkspaceQuery(query)
  return resolved !== null && "identity" in resolved
}

// Containment check: is `candidate` nested strictly under the runner root
// (no `..` traversal, no absolute leak)? Used
// by the cleanup loop to refuse deleting workspaces outside the runner
// root, and by the manual `RemoveWorkspace` handler for the same guard.
export function isUnderRunnerRoot(root: string, candidate: string): boolean {
  const rootPath = resolve(root)
  const target = resolve(candidate)
  const rel = relative(rootPath, target)
  return rel !== "" && !rel.startsWith("..") && !isAbsolute(rel)
}
