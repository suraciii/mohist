export interface WorkflowSessionTurnKey {
  projectId: string
  workflowRunId: string
  sessionName: string
}

export class WorkflowSessionTurnCoordinator {
  private readonly tails = new Map<string, Promise<void>>()

  async withTurn<T>(key: WorkflowSessionTurnKey, turn: () => Promise<T>): Promise<T> {
    const queueKey = JSON.stringify([key.projectId, key.workflowRunId, key.sessionName])
    const predecessor = this.tails.get(queueKey) ?? Promise.resolve()
    let release!: () => void
    const current = new Promise<void>((resolve) => { release = resolve })
    const tail = predecessor.catch(() => undefined).then(() => current)
    this.tails.set(queueKey, tail)

    await predecessor.catch(() => undefined)
    try {
      return await turn()
    } finally {
      release()
      if (this.tails.get(queueKey) === tail) this.tails.delete(queueKey)
    }
  }

  sizeForTest(): number {
    return this.tails.size
  }
}
