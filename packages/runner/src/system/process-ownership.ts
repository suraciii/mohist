import { AsyncLocalStorage } from 'node:async_hooks'

const workspaceProcessOwner = new AsyncLocalStorage<() => void>()

export function withWorkspaceProcessOwner<T>(onCommandStart: () => void, work: () => T): T {
  return workspaceProcessOwner.run(onCommandStart, work)
}

export function noteWorkspaceCommandStart(): void {
  workspaceProcessOwner.getStore()?.()
}
