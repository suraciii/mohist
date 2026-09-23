export { useCloseWorkspace, useWorkspace, useWorkspaces } from './api/queries'
export { closeWorkspace, getWorkspace, getWorkspaces } from './api/client'
export { workspaceOriginLabel } from './model/origin'
export type {
  Workspace,
  WorkspaceDirectoryObservation,
  WorkspaceHome,
  WorkspaceOrigin,
  WorkspaceSession,
  WorkspaceStatus,
} from './model/types'
