import { realpathSync } from 'node:fs'
import { isAbsolute, relative, resolve } from 'node:path'
import type { WorkspaceRemovalFence, WorkspaceRemovalFenceResult } from './workspace-removal-fence.js'

export class WorkspaceUseConflict extends Error {
  constructor() {
    super('Workspace removal is in progress; retry this operation')
  }
}

export class RunnerWorkspaceUse implements WorkspaceRemovalFence {
  private readonly users = new Map<string, number>()
  private readonly removing = new Set<string>()
  private readonly previousGeneration = new Set<string>()
  private unknownPreviousGeneration = false

  constructor(
    private readonly releaseResources: (workspacePath: string) => Promise<'ready' | 'busy' | 'failed'>,
    private readonly realpath: (path: string) => string = realpathSync,
    private readonly onAdmit?: (workspacePath: string) => void,
  ) {}

  acquire(workspacePath: string): () => void {
    const key = resolve(workspacePath)
    if (this.removing.has(key)) throw new WorkspaceUseConflict()
    this.users.set(key, (this.users.get(key) ?? 0) + 1)
    this.onAdmit?.(key)
    let released = false
    return () => {
      if (released) return
      released = true
      const remaining = (this.users.get(key) ?? 1) - 1
      if (remaining === 0) this.users.delete(key)
      else this.users.set(key, remaining)
    }
  }

  async withUse<T>(workspacePath: string, work: () => Promise<T>): Promise<T> {
    const release = this.acquire(workspacePath)
    try {
      return await work()
    } finally {
      release()
    }
  }

  async withRemovalFence<T>(
    workspacePath: string,
    callback: () => Promise<T>,
  ): Promise<WorkspaceRemovalFenceResult<T>> {
    const key = resolve(workspacePath)
    if (this.unknownPreviousGeneration || this.previousGeneration.has(key))
      return { kind: 'failed', reason: 'previous_generation_unconfirmed' }
    if (this.removing.has(key) || (this.users.get(key) ?? 0) > 0) return { kind: 'busy' }
    this.removing.add(key)
    try {
      const readiness = await this.releaseResources(key)
      if (readiness !== 'ready') return { kind: readiness }
      return { kind: 'completed', value: await callback() }
    } catch {
      return { kind: 'failed' }
    } finally {
      this.removing.delete(key)
    }
  }

  blockPreviousGeneration(workspacePaths: readonly string[]): void {
    for (const path of workspacePaths) this.previousGeneration.add(resolve(path))
  }

  blockUnknownPreviousGeneration(): void {
    this.unknownPreviousGeneration = true
  }

  // A live /proc directory handle resolves to its physical Workspace path.
  // A stale handle is unknown, never a path that may be admitted by name alone.
  ownerForWorkDir(workDir: string, workspacePaths: readonly string[]): string | null {
    let physical: string
    try {
      physical = this.realpath(workDir)
    } catch {
      return null
    }
    for (const candidate of workspacePaths) {
      let root: string
      try {
        root = this.realpath(candidate)
      } catch {
        continue
      }
      const child = relative(root, physical)
      if (child === '' || (child !== '..' && !child.startsWith('../') && !isAbsolute(child))) return resolve(candidate)
    }
    return null
  }
}
