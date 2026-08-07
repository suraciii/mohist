import type { WorkspaceOrigin } from './types'

export function workspaceOriginLabel(origin: WorkspaceOrigin): string {
  switch (origin.kind) {
    case 'issue':
      return `Issue #${origin.issueNumber ?? '?'}`
    case 'slack':
      return 'Slack'
    case 'web':
      return 'Web'
    case 'manual':
      return 'Manual'
    default:
      return 'Unknown'
  }
}
