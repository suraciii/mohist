import type { NamedWorkspaceManager } from '../../src/runtime/workspace-entity.js'

export interface WorkspaceInfo {
  path: string
  branch?: string | null
}

// Test helper: build a `NamedWorkspaceManager` mock that satisfies the
// contract used by `WorkExecutor`. Tests that just want to say "the
// workspace is ready, here it is" plug in
// `verifyOnlyNamedWorkspaceManager(...)` so materialization returns the
// supplied path without touching git.
export function verifyOnlyNamedWorkspaceManager(workspace: WorkspaceInfo): NamedWorkspaceManager {
  const info = { path: workspace.path, branch: workspace.branch ?? null }
  return {
    provisionForIssue: async () => info,
    provision: async () => info,
  } as unknown as NamedWorkspaceManager
}

// Retained alias for call sites written against the removed preparer seam.
export const verifyOnlyWorkspacePreparer = verifyOnlyNamedWorkspaceManager
