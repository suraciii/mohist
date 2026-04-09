import type { ModelMessage } from 'ai';

export type SessionStatus = 'active' | 'paused' | 'closed';

export interface Session {
  id: string;
  issueId: number;
  messages: ModelMessage[];
  createdAt: Date;
  closedAt: Date | null;
  status: SessionStatus;
  metadata: Record<string, unknown>;
}

function generateSessionId(): string {
  return `sess_${Date.now()}_${Math.random().toString(36).slice(2, 10)}`;
}

export class SessionManager {
  private sessions = new Map<string, Session>();

  create(issueId: number): Session {
    const session: Session = {
      id: generateSessionId(),
      issueId,
      messages: [],
      createdAt: new Date(),
      closedAt: null,
      status: 'active',
      metadata: {},
    };
    this.sessions.set(session.id, session);
    return session;
  }

  appendMessage(sessionId: string, message: ModelMessage): void {
    const session = this.sessions.get(sessionId);
    if (!session) {
      throw new Error(`Session not found: ${sessionId}`);
    }
    if (session.status === 'closed') {
      throw new Error(`Session is closed: ${sessionId}`);
    }
    session.messages.push(message);
  }

  getMessages(sessionId: string): ModelMessage[] {
    const session = this.sessions.get(sessionId);
    if (!session) {
      throw new Error(`Session not found: ${sessionId}`);
    }
    return session.messages;
  }

  get(sessionId: string): Session | undefined {
    return this.sessions.get(sessionId);
  }

  close(sessionId: string): void {
    const session = this.sessions.get(sessionId);
    if (!session) {
      throw new Error(`Session not found: ${sessionId}`);
    }
    session.closedAt = new Date();
    session.status = 'closed';
  }

  pause(sessionId: string): void {
    const session = this.sessions.get(sessionId);
    if (!session) {
      throw new Error(`Session not found: ${sessionId}`);
    }
    if (session.status === 'closed') {
      throw new Error(`Cannot pause a closed session: ${sessionId}`);
    }
    session.status = 'paused';
  }

  resume(sessionId: string): void {
    const session = this.sessions.get(sessionId);
    if (!session) {
      throw new Error(`Session not found: ${sessionId}`);
    }
    if (session.status !== 'paused') {
      throw new Error(`Cannot resume a session that is not paused: ${sessionId}`);
    }
    session.status = 'active';
  }

  findByIssueId(issueId: number): Session | undefined {
    for (const session of this.sessions.values()) {
      if (session.issueId === issueId && session.status !== 'closed') {
        return session;
      }
    }
    return undefined;
  }

  remove(sessionId: string): void {
    this.sessions.delete(sessionId);
  }

  clear(): void {
    this.sessions.clear();
  }
}
