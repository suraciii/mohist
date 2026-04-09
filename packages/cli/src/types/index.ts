export enum Stage {
  Draft = 'draft',
  Plan = 'plan',
  Review = 'review',
  Build = 'build',
  Check = 'check',
  Done = 'done'
}

export enum IssueStatus {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked'
}

export interface Issue {
  id: string;
  number: number;
  title: string;
  body?: string;
  stage: Stage;
  status: IssueStatus;
  projectId: string;
  labels: string[];
  createdAt: string;
  updatedAt: string;
}

export interface Project {
  id: string;
  name: string;
  path: string;
  baseBranch: string;
  createdAt: string;
  updatedAt: string;
}

export interface Config {
  serverPort: number;
  pollInterval: number;
  maxConcurrentAgents: number;
  agentTimeout: number;
}

export interface ServerState {
  isRunning: boolean;
  pid?: number;
  port: number;
  startedAt?: string;
}

export interface ApiResponse<T = any> {
  success: boolean;
  data?: T;
  error?: string;
}

export interface Comment {
  id: string;
  issueId: string;
  body: string;
  createdAt: string;
}

export type QuestionStatus = 'pending' | 'answered' | 'expired';

export interface Question {
  id: string;
  issueId: string;
  question: string;
  answer?: string;
  status: QuestionStatus;
  createdAt: string;
  answeredAt?: string;
}

export enum ExploreStatus {
  Active = 'active',
  Crystallized = 'crystallized',
  Archived = 'archived',
}

export interface ExploreSession {
  id: string;
  projectId: string;
  issueId: string | null;
  title: string;
  status: ExploreStatus;
  createdAt: string;
  updatedAt: string;
}

export interface ToolCallRecord {
  name: string;
  args: Record<string, unknown>;
  result: unknown;
}

export interface ExploreMessage {
  id: string;
  sessionId: string;
  role: 'user' | 'assistant';
  content: string;
  toolCalls: ToolCallRecord[] | null;
  createdAt: string;
}
