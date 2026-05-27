import type { AcpProcessHandle } from "../actions/acp-agent.js"
import type { ClientSideConnection } from "@agentclientprotocol/sdk"

export interface PooledSession {
  process: AcpProcessHandle
  connection: ClientSideConnection
  sessionId: string
  model?: string
  workDir: string
}

export class AcpSessionPool {
  private sessions = new Map<string, PooledSession>()

  key(workflowRunId: string, sessionName: string): string {
    return `${workflowRunId}:${sessionName}`
  }

  get(key: string): PooledSession | undefined {
    return this.sessions.get(key)
  }

  set(key: string, session: PooledSession): void {
    this.sessions.set(key, session)
  }

  has(key: string): boolean {
    return this.sessions.has(key)
  }

  async close(key: string): Promise<void> {
    const session = this.sessions.get(key)
    if (!session) return
    this.sessions.delete(key)
    await cleanupPooledSession(session)
  }

  async closeAllForWorkflow(workflowRunId: string): Promise<void> {
    const prefix = `${workflowRunId}:`
    const toClose = [...this.sessions.entries()].filter(([k]) => k.startsWith(prefix))
    for (const [key] of toClose) this.sessions.delete(key)
    await Promise.allSettled(toClose.map(([, s]) => cleanupPooledSession(s)))
  }

  async closeAll(): Promise<void> {
    const all = [...this.sessions.values()]
    this.sessions.clear()
    await Promise.allSettled(all.map(cleanupPooledSession))
  }
}

async function cleanupPooledSession(session: PooledSession): Promise<void> {
  try {
    await session.connection.closeSession?.({ sessionId: session.sessionId })
  } catch {}
  await session.process.cleanup()
}
