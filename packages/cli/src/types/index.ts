export enum Stage {
  Explore = 'explore',
  Plan = 'plan',
  Build = 'build',
  Review = 'review',
  Done = 'done',
  Draft = 'draft',
  Check = 'check'
}

export const STAGE_ORDER: Stage[] = [
  Stage.Explore,
  Stage.Plan,
  Stage.Build,
  Stage.Review,
  Stage.Done
];

export const STAGE_TRANSITIONS: Record<Stage, Stage[]> = {
  [Stage.Explore]: [Stage.Plan],
  [Stage.Plan]: [Stage.Build],
  [Stage.Build]: [Stage.Review],
  [Stage.Review]: [Stage.Done, Stage.Build],
  [Stage.Done]: [],
  [Stage.Draft]: [Stage.Plan],
  [Stage.Check]: [Stage.Done, Stage.Plan]
};

export function isValidTransition(from: Stage, to: Stage): boolean {
  const allowed = STAGE_TRANSITIONS[from];
  return allowed?.includes(to) ?? false;
}

export enum IssueStatus {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked'
}

export type ApprovalStatus = 'pending' | 'awaiting' | 'approved' | 'rejected';

export interface ApprovalState {
  stage: Stage;
  status: ApprovalStatus;
  output: unknown;
  requestedAt: string;
  respondedAt?: string;
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
  approvalState?: ApprovalState;
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
