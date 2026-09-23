import { realpathSync } from 'node:fs'
import { isAbsolute, relative, resolve } from 'node:path'
import type { CodexTurnCompletedEvent } from './protocol-types.js'

interface WorkspaceOperation {
  generation: number
  threadId: string
  turnId: string | null
  workDir: string | null
  earlyCompletedTurnIds: Set<string>
}

export class CodexWorkspaceUse {
  private readonly operations = new Set<WorkspaceOperation>()

  begin(generation: number, threadId: string, workDir: string): WorkspaceOperation {
    const operation: WorkspaceOperation = {
      generation,
      threadId,
      turnId: null,
      workDir: canonicalWorkDir(workDir),
      earlyCompletedTurnIds: new Set(),
    }
    this.operations.add(operation)
    return operation
  }

  bindTurnId(operation: WorkspaceOperation, turnId: string): void {
    if (!this.operations.has(operation)) return
    operation.turnId = turnId
    if (operation.earlyCompletedTurnIds.has(turnId)) this.operations.delete(operation)
    operation.earlyCompletedTurnIds.clear()
  }

  completed(generation: number, event: CodexTurnCompletedEvent): void {
    for (const operation of this.operations) {
      if (operation.generation !== generation || operation.threadId !== event.threadId) continue
      if (operation.turnId === null) {
        operation.earlyCompletedTurnIds.add(event.turnId)
      } else if (operation.turnId === event.turnId) {
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
