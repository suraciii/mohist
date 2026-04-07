import { ExploreSession, ExploreMessage, ToolCallRecord } from '../types';
import { ExploreSessionRepo, ExploreMessageRepo } from '../db';

export interface CreateExploreSessionInput {
  projectId: string;
  title: string;
}

export interface ExploreSessionWithMessages {
  session: ExploreSession;
  messages: ExploreMessage[];
}

export class ExploreService {
  constructor(
    private sessionRepo: ExploreSessionRepo,
    private messageRepo: ExploreMessageRepo
  ) {}

  createSession(input: CreateExploreSessionInput): ExploreSession {
    return this.sessionRepo.create({
      projectId: input.projectId,
      title: input.title,
    });
  }

  listSessions(projectId: string): ExploreSession[] {
    return this.sessionRepo.findByProject(projectId);
  }

  getSession(id: string): ExploreSessionWithMessages | null {
    const session = this.sessionRepo.findById(id);
    if (!session) return null;

    const messages = this.messageRepo.findBySession(id);
    return { session, messages };
  }

  deleteSession(id: string): boolean {
    this.messageRepo.deleteBySession(id);
    return this.sessionRepo.delete(id);
  }

  addMessage(
    sessionId: string,
    role: 'user' | 'assistant',
    content: string,
    toolCalls?: ToolCallRecord[]
  ): ExploreMessage {
    return this.messageRepo.create({
      sessionId,
      role,
      content,
      toolCalls,
    });
  }

  getMessages(sessionId: string): ExploreMessage[] {
    return this.messageRepo.findBySession(sessionId);
  }

  crystallize(sessionId: string, issueId: string): ExploreSession | null {
    return this.sessionRepo.crystallize(sessionId, issueId);
  }
}
