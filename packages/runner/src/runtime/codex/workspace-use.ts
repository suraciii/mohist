import { realpathSync } from 'node:fs'
import { isAbsolute, relative, resolve } from 'node:path'
import type { CodexTurnCompletedEvent } from './protocol-types.js'

interface WorkspaceOperation {
  threadId: string
  turnId: string | null
  workDir: string | null
}

export class CodexWorkspaceUse {
  private readonly operations = new Set<WorkspaceOperation>()

  begin(threadId: string, workDir: string): WorkspaceOperation {
    const operation: WorkspaceOperation = { threadId, turnId: null, workDir: canonicalWorkDir(workDir) }
    this.operations.add(operation)
    return operation
  }

  completed(event: CodexTurnCompletedEvent): void {
    for (const operation of this.operations) {
      if (operation.threadId === event.threadId && operation.turnId === event.turnId) {
        this.operations.delete(operation)
      }
    }
  }

  inspect(workspacePath: string): 'ready' | 'busy' | 'failed' {
    const root = canonicalWorkDir(workspacePath)
    if (root === null) return 'failed'
    for (const operation of this.operations) {
      if (operation.workDir === null) return 'failed'
      const child = relative(root, operation.workDir)
      if (child === '' || (child !== '..' && !child.startsWith('../') && !isAbsolute(child))) return 'busy'
    }
    return 'ready'
  }
}

function canonicalWorkDir(value: string): string | null {
  try {
    return realpathSync(value)
  } catch {
    return value.startsWith('/proc/') ? null : resolve(value)
  }
}
