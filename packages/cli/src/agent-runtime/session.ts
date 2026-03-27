import type { ModelMessage } from 'ai';

export interface Session {
  id: string;
  issueId: number;
  messages: ModelMessage[];
  createdAt: Date;
  closedAt: Date | null;
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
    };
    this.sessions.set(session.id, session);
    return session;
  }

  appendMessage(sessionId: string, message: ModelMessage): void {
    const session = this.sessions.get(sessionId);
    if (!session) {
      throw new Error(`Session not found: ${sessionId}`);
    }
    if (session.closedAt) {
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
  }

  remove(sessionId: string): void {
    this.sessions.delete(sessionId);
  }

  clear(): void {
    this.sessions.clear();
  }
}
