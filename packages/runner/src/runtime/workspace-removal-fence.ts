export type WorkspaceRemovalFenceResult<T> =
  | { readonly kind: 'completed'; readonly value: T }
  | { readonly kind: 'busy' }
  | { readonly kind: 'failed'; readonly reason?: string }

export interface WorkspaceRemovalFence {
  withRemovalFence<T>(workspacePath: string, callback: () => Promise<T>): Promise<WorkspaceRemovalFenceResult<T>>
}
