import { isAbsolute, relative, resolve } from "node:path"

// Wire shape for workspace-scoped SignalR queries. `workspacePath` is the
// on-disk worktree the runner materialized; `branch` is the head ref the
// dispatch put into the worktree (e.g. `mohist/run-${workflowRunId}`);
// `baseBranch` is the upstream branch the server-side review APIs diff
// against. `issueNumber` is preserved for log/telemetry only — it is NOT
// used to derive a head ref. The legacy `mo/issue-${issueNumber}` branch
// is no longer materialized and MUST NOT be used as a fallback.
export interface WorkspaceQuery {
  issueNumber?: number
  workspacePath?: string | null
  branch?: string | null
  baseBranch?: string | null
}

// Resolve a workspace query into the triple every git-backed handler
// needs. Returns `null` when ANY of `workspacePath` / `baseBranch` /
// `branch` is missing — the server-side review APIs surface that as
// `branch_missing` rather than the handler falling through to a phantom
// ref. The resolver MUST NOT synthesize a head ref from `issueNumber`:
// the legacy `mo/issue-${N}` worktree branch is no longer created by the
// runner, so a phantom `mo/issue-${N}` ref would never resolve.
export function resolveWorkspaceQuery(
  query: WorkspaceQuery | null | undefined,
): { workDir: string; baseBranch: string; head: string } | null {
  if (!query?.workspacePath || !query.baseBranch) return null
  const head = query.branch ?? null
  if (!head) return null
  return { workDir: query.workspacePath, baseBranch: query.baseBranch, head }
}

// Containment check: is `candidate` the runner root itself, OR a path
// nested strictly under it (no `..` traversal, no absolute leak)? Used
// by the cleanup loop to refuse deleting workspaces outside the runner
// root, and by the manual `RemoveWorkspace` handler for the same guard.
export function isUnderRunnerRoot(root: string, candidate: string): boolean {
  const rootPath = resolve(root)
  const target = resolve(candidate)
  const rel = relative(rootPath, target)
  return rel === "" || (!rel.startsWith("..") && !isAbsolute(rel))
}